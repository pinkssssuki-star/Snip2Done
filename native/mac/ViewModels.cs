using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace RemeberEr
{
    /// <summary>
    /// 缩略图缓存。**按目标宽度解码**是关键 —— 一张 2560×1440 的 PNG 原样解码要十几 MB，
    /// 而卡片上只显示 46×34。Avalonia 的 Bitmap.DecodeToWidth 在解码阶段就缩放，
    /// 能把常驻内存压下来一个量级（对应 WPF 版的 DecodePixelWidth）。
    ///
    /// 另外解码一律走后台：PNG 解码是纯 CPU 的，放在 UI 线程上会把启动时间直接拖垮
    /// （原版实测 11 张图同步解码要 775ms）。
    /// </summary>
    public static class Images
    {
        static readonly Dictionary<string, Bitmap> _cache = new Dictionary<string, Bitmap>();
        static readonly object _gate = new object();

        static bool TryCached(string key, out Bitmap hit)
        {
            lock (_gate) { return _cache.TryGetValue(key, out hit); }
        }

        static void Store(string key, Bitmap img)
        {
            lock (_gate) { _cache[key] = img; }
        }

        /// <summary>
        /// 同步取图（已经缓存时便宜；没缓存则阻塞解码）。
        ///
        /// 文件先整个读进 MemoryStream 再交给解码器：这样解码器不会持有文件句柄，
        /// 用户删除/替换条目图片时不会遇到「文件被占用」。
        /// </summary>
        public static Bitmap Get(string absolutePath, int decodeWidth)
        {
            if (string.IsNullOrEmpty(absolutePath) || !File.Exists(absolutePath)) return null;
            string key = absolutePath + "|" + decodeWidth;
            Bitmap hit;
            if (TryCached(key, out hit)) return hit;

            try
            {
                byte[] bytes = File.ReadAllBytes(absolutePath);
                using (var ms = new MemoryStream(bytes, false))
                {
                    Bitmap bmp = decodeWidth > 0
                        ? Bitmap.DecodeToWidth(ms, decodeWidth, BitmapInterpolationMode.MediumQuality)
                        : new Bitmap(ms);
                    Store(key, bmp);
                    return bmp;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// 后台解码，解完回 UI 线程交给回调。缓存命中或路径为空时同步回调。
        ///
        /// 刻意用「一条低优先级后台线程」而不是线程池：线程池会一次性并发解十几张 PNG，
        /// 把 CPU 抢光，结果 UI 线程自己的 XAML 解析和首帧渲染反而被拖慢
        /// （原版实测并发解码时首帧 1015ms，串行低优先级之后回到 500ms 量级）。
        /// </summary>
        public static void GetAsync(string absolutePath, int decodeWidth, Action<Bitmap> done)
        {
            if (string.IsNullOrEmpty(absolutePath) || !File.Exists(absolutePath))
            {
                if (done != null) done(null);
                return;
            }
            string key = absolutePath + "|" + decodeWidth;
            Bitmap hit;
            if (TryCached(key, out hit)) { if (done != null) done(hit); return; }

            Enqueue(() =>
            {
                var img = Get(absolutePath, decodeWidth);
                if (done == null) return;
                // 回 UI 线程再交给调用方：缩略图要写进被绑定的 VM 属性，
                // 跨线程改属性会让绑定系统抛异常。
                Avalonia.Threading.Dispatcher.UIThread.Post(() => done(img));
            });
        }

        // ---- 单线程解码队列 ----

        static readonly Queue<Action> _jobs = new Queue<Action>();
        static readonly object _jobGate = new object();
        static System.Threading.Thread _worker;

        static void Enqueue(Action job)
        {
            lock (_jobGate)
            {
                _jobs.Enqueue(job);
                if (_worker != null) return;
                _worker = new System.Threading.Thread(WorkerLoop)
                {
                    IsBackground = true,
                    Priority = System.Threading.ThreadPriority.BelowNormal,
                    Name = "thumb-decode",
                };
                _worker.Start();
            }
        }

        static void WorkerLoop()
        {
            while (true)
            {
                Action job = null;
                lock (_jobGate) { if (_jobs.Count > 0) job = _jobs.Dequeue(); }
                if (job == null) { System.Threading.Thread.Sleep(15); continue; }
                try { job(); }
                catch (Exception) { }
            }
        }

        public static void Clear()
        {
            lock (_gate) { _cache.Clear(); }
        }
    }

    /// <summary>时间/时长的人话格式化 —— 和 panel.js 里的 relTime / fmtDur 一一对应。</summary>
    public static class Fmt
    {
        public static string RelTime(double ts)
        {
            double m = Math.Floor((Store.Now() - ts) / 60000.0);
            if (m < 1) return "刚刚";
            if (m < 60) return (long)m + " 分钟前";
            double h = Math.Floor(m / 60);
            if (h < 24) return (long)h + " 小时前";
            double d = Math.Floor(h / 24);
            if (d < 30) return (long)d + " 天前";
            return DateTimeOffset.FromUnixTimeMilliseconds((long)ts).ToLocalTime().ToString("yyyy/M/d");
        }

        public static string Dur(double ms)
        {
            if (ms < 0) return "—";
            double s = Math.Floor(ms / 1000);
            if (s < 60) return (long)s + " 秒";
            double m = Math.Floor(s / 60);
            if (m < 60) return (long)m + " 分钟";
            double h = Math.Floor(m / 60);
            if (h < 24) return (m % 60 > 0) ? (long)h + " 小时 " + (long)(m % 60) + " 分" : (long)h + " 小时";
            double d = Math.Floor(h / 24);
            if (d < 30) return (h % 24 > 0) ? (long)d + " 天 " + (long)(h % 24) + " 小时" : (long)d + " 天";
            double mo = Math.Floor(d / 30);
            return (d % 30 > 0) ? (long)mo + " 个月 " + (long)(d % 30) + " 天" : (long)mo + " 个月";
        }

        public static string DayLabel(double ts)
        {
            var d = DateTimeOffset.FromUnixTimeMilliseconds((long)ts).ToLocalTime().DateTime;
            var today = DateTime.Today;
            int diff = (int)Math.Round((today - d.Date).TotalDays);
            if (diff == 0) return "今天";
            if (diff == 1) return "昨天";
            if (diff == 2) return "前天";
            string[] wd = { "周日", "周一", "周二", "周三", "周四", "周五", "周六" };
            return (d.Month) + " 月 " + d.Day + " 日 · " + wd[(int)d.DayOfWeek];
        }

        public static string DayKey(double ts)
        {
            var d = DateTimeOffset.FromUnixTimeMilliseconds((long)ts).ToLocalTime().DateTime;
            return d.Year + "-" + d.Month + "-" + d.Day;
        }
    }

    // ------------------------------------------------------------------ 条目 VM

    public class TodoVm : ObservableObject
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public bool HasTitle { get { return !string.IsNullOrWhiteSpace(Title); } }
        public bool IsDone { get; set; }
        public bool Overdue { get; set; }
        public string PillText { get; set; }
        public bool HasPill { get { return !string.IsNullOrEmpty(PillText); } }
        /// <summary>"today" / "soon" / "bad" —— 对应 HTML 版 .pill 的三个配色</summary>
        public string PillKind { get; set; }
        public string MetaSource { get; set; }
        public bool ShowMetaSource { get { return !HasTitle && !string.IsNullOrEmpty(MetaSource); } }
        public string MetaTime { get; set; }
        Bitmap _thumb;
        public Bitmap Thumb
        {
            get { return _thumb; }
            set { _thumb = value; Raise("Thumb"); Raise("HasThumb"); }
        }
        public string ImagePath { get; set; }
        public bool HasThumb { get { return Thumb != null; } }
        public bool CanDissolve { get { return IsDone; } }
    }

    public class WantVm : ObservableObject
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Caption { get; set; }
        Bitmap _thumb;
        public Bitmap Thumb
        {
            get { return _thumb; }
            set { _thumb = value; Raise("Thumb"); Raise("HasThumb"); }
        }
        public string ImagePath { get; set; }
        public bool HasThumb { get { return Thumb != null; } }
        public string FirstChar { get; set; }
        public double Opacity { get; set; }
        public double LifePercent { get; set; }
        public string FadingHint { get; set; }
        public bool ShowFadingHint { get { return !string.IsNullOrEmpty(FadingHint); } }
        public Bitmap BigImage { get { return Thumb; } }
    }

    public class BinVm : ObservableObject
    {
        public string Id { get; set; }
        public string Caption { get; set; }
        public string RoomName { get; set; }
        public string ChipKind { get; set; }
        Bitmap _thumb;
        public Bitmap Thumb
        {
            get { return _thumb; }
            set { _thumb = value; Raise("Thumb"); Raise("HasThumb"); }
        }
        public string ImagePath { get; set; }
        public bool HasThumb { get { return Thumb != null; } }
        public string FirstChar { get; set; }
    }

    public class DoneVm : ObservableObject
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string RoomName { get; set; }
        public string ChipKind { get; set; }
        public string MetaText { get; set; }
        Bitmap _thumb;
        public Bitmap Thumb
        {
            get { return _thumb; }
            set { _thumb = value; Raise("Thumb"); Raise("HasThumb"); }
        }
        public string ImagePath { get; set; }
        public bool HasThumb { get { return Thumb != null; } }
        public string FirstChar { get; set; }
    }

    public class DoneGroupVm
    {
        public string Label { get; set; }
        public int Count { get; set; }
        public ObservableCollection<DoneVm> Items { get; set; }
    }

    // ------------------------------------------------------------------ 面板 VM

    public class PanelViewModel : ObservableObject
    {
        readonly Store _store;

        public ObservableCollection<TodoVm> Todos { get; private set; }
        public ObservableCollection<WantVm> Wants { get; private set; }
        public ObservableCollection<BinVm> Bins { get; private set; }
        public ObservableCollection<DoneGroupVm> DoneGroups { get; private set; }

        public Dictionary<string, object> Raw { get; private set; }

        public PanelViewModel(Store store)
        {
            _store = store;
            Todos = new ObservableCollection<TodoVm>();
            Wants = new ObservableCollection<WantVm>();
            Bins = new ObservableCollection<BinVm>();
            DoneGroups = new ObservableCollection<DoneGroupVm>();

            SnipCmd = new RelayCommand(() => { if (SnipRequested != null) SnipRequested(); });
            ToggleQuickCmd = new RelayCommand(ToggleQuick);
            DemoToggleCmd = new RelayCommand(ToggleDemo);
            SettingsCmd = new RelayCommand(() => { View = View == "set" ? "main" : "set"; OpacityPreview = null; });
            CollapseCmd = new RelayCommand(() => { if (CollapseRequested != null) CollapseRequested(); });
            ToggleTopmostCmd = new RelayCommand(ToggleTopmost);
            QuitCmd = new RelayCommand(() => { if (QuitRequested != null) QuitRequested(); });
            ShowBinCmd = new RelayCommand(() => View = "bin");
            ShowDoneCmd = new RelayCommand(() => View = "done");
            BackCmd = new RelayCommand(() => { View = "main"; OpacityPreview = null; });
            SeedCmd = new RelayCommand(() => { _store.SeedDemo(); Refresh(); });
            AddManualCmd = new RelayCommand(AddManual);
            SetRoomCmd = new RelayCommand(p => QuickRoom = (string)p);

            ToggleDoneCmd = new RelayCommand(p => { _store.Done((string)p, false, null, false); Refresh(); });
            BinItemCmd = new RelayCommand(p => { _store.Bin((string)p); Refresh(); });
            RestoreCmd = new RelayCommand(p => { _store.Restore((string)p); Refresh(); });
            OpenCmd = new RelayCommand(p => Open((string)p));
            OpenDoneCmd = new RelayCommand(p => { if (OpenRecordRequested != null) OpenRecordRequested((string)p); });
        }

        public event Action SnipRequested;
        public event Action CollapseRequested;
        public event Action QuitRequested;

        /// <summary>
        /// 有一张缩略图解码完成，界面可以重画了。
        ///
        /// 为什么不复用 Refresh()：解码回填只改了 VM 上的 Thumb 一个字段，
        /// 为此重读一遍 JSON 再重建所有集合是浪费（原版也是这么分的）。
        /// </summary>
        public event Action ImageRefreshNeeded;
        public event Action<string> OpenDetailRequested;
        public event Action<string> OpenRecordRequested;

        public RelayCommand SnipCmd { get; private set; }
        public RelayCommand ToggleQuickCmd { get; private set; }
        public RelayCommand DemoToggleCmd { get; private set; }
        public RelayCommand SettingsCmd { get; private set; }
        public RelayCommand CollapseCmd { get; private set; }
        public RelayCommand ToggleTopmostCmd { get; private set; }
        public RelayCommand QuitCmd { get; private set; }
        public RelayCommand ShowBinCmd { get; private set; }
        public RelayCommand ShowDoneCmd { get; private set; }
        public RelayCommand BackCmd { get; private set; }
        public RelayCommand SeedCmd { get; private set; }
        public RelayCommand AddManualCmd { get; private set; }
        public RelayCommand SetRoomCmd { get; private set; }
        public RelayCommand ToggleDoneCmd { get; private set; }
        public RelayCommand BinItemCmd { get; private set; }
        public RelayCommand RestoreCmd { get; private set; }
        public RelayCommand OpenCmd { get; private set; }
        public RelayCommand OpenDoneCmd { get; private set; }

        // ---------------- 视图状态 ----------------

        string _view = "main";
        public string View
        {
            get { return _view; }
            set
            {
                if (!Set(ref _view, value, "View")) return;
                Raise("IsMainView"); Raise("IsBinView"); Raise("IsDoneView"); Raise("IsSetView");
                Raise("SettingsOn");
            }
        }

        public bool IsMainView { get { return _view == "main"; } }
        public bool IsBinView { get { return _view == "bin"; } }
        public bool IsDoneView { get { return _view == "done"; } }
        public bool IsSetView { get { return _view == "set"; } }
        public bool SettingsOn { get { return _view == "set"; } }

        bool _collapsed;
        public bool Collapsed
        {
            get { return _collapsed; }
            set { if (Set(ref _collapsed, value, "Collapsed")) Raise("CollapseGlyph"); }
        }
        public string CollapseGlyph { get { return _collapsed ? "▸" : "▾"; } }

        bool _quickOpen;
        public bool QuickOpen { get { return _quickOpen; } set { Set(ref _quickOpen, value, "QuickOpen"); } }

        string _quickRoom = "todo";
        public string QuickRoom
        {
            get { return _quickRoom; }
            set
            {
                if (!Set(ref _quickRoom, value, "QuickRoom")) return;
                Raise("QuickTodoOn"); Raise("QuickWantOn");
            }
        }
        public bool QuickTodoOn { get { return _quickRoom == "todo"; } }
        public bool QuickWantOn { get { return _quickRoom == "want"; } }

        string _quickText = "";
        public string QuickText { get { return _quickText; } set { Set(ref _quickText, value, "QuickText"); } }

        // ---------------- 计数 / 统计 ----------------

        int _nTodo, _nWant, _nBin, _nDone;
        public int NTodo { get { return _nTodo; } set { Set(ref _nTodo, value, "NTodo"); } }
        public int NWant { get { return _nWant; } set { Set(ref _nWant, value, "NWant"); } }
        public int NBin { get { return _nBin; } set { Set(ref _nBin, value, "NBin"); } }
        public int NDone { get { return _nDone; } set { Set(ref _nDone, value, "NDone"); } }

        // 空态用：绑到对应集合的 Count 上，比在 XAML 里比较 Count 省事
        public bool HasTodos { get { return Todos.Count > 0; } }
        public bool HasWants { get { return Wants.Count > 0; } }
        public bool HasBins { get { return Bins.Count > 0; } }
        public bool HasDoneGroups { get { return DoneGroups.Count > 0; } }

        string _stats = "—";
        public string Stats { get { return _stats; } set { Set(ref _stats, value, "Stats"); } }

        bool _demoOn;
        public bool DemoOn { get { return _demoOn; } set { Set(ref _demoOn, value, "DemoOn"); } }

        bool _showSeedRow;
        public bool ShowSeedRow { get { return _showSeedRow; } set { Set(ref _showSeedRow, value, "ShowSeedRow"); } }

        /// <summary>
        /// 快速模式：框选松手即定，不再需要点右下角那个 ✓。
        /// 「一框，一分类」—— 对熟手来说少一步就是少一步。
        /// </summary>
        bool _quickCapture;
        public bool QuickCapture
        {
            get { return _quickCapture; }
            set
            {
                if (!Set(ref _quickCapture, value, "QuickCapture")) return;
                _store.SetSetting("quickCapture", value);
            }
        }

        /// <summary>
        /// 是否一直压在别的窗口上面。
        /// 单屏用户不需要它一直压着 —— 那会挡住他们真正在用的东西，所以给了开关。
        /// </summary>
        bool _alwaysOnTop = true;
        public bool AlwaysOnTop
        {
            get { return _alwaysOnTop; }
            set
            {
                if (!Set(ref _alwaysOnTop, value, "AlwaysOnTop")) return;
                _store.SetSetting("alwaysOnTop", value);
                var h = TopmostChanged;
                if (h != null) h(value);
            }
        }

        /// <summary>置顶开关变了，窗口自己去设 Topmost。</summary>
        public event Action<bool> TopmostChanged;

        public void ToggleTopmost() { AlwaysOnTop = !_alwaysOnTop; }

        /// <summary>贴边隐藏中（只露一条窄边在外面）。</summary>
        bool _isDocked;
        public bool IsDocked
        {
            get { return _isDocked; }
            set
            {
                if (!Set(ref _isDocked, value, "IsDocked")) return;
                Raise("DockHintVisible");
            }
        }

        string _dockHint;
        public string DockHint
        {
            get { return _dockHint; }
            set
            {
                if (!Set(ref _dockHint, value ?? "", "DockHint")) return;
                Raise("DockHintVisible");
            }
        }
        public bool DockHintVisible { get { return !string.IsNullOrEmpty(_dockHint); } }

        // ---------------- 设置 ----------------

        double _panelAlpha = 0.8;
        public double PanelAlpha
        {
            get { return _panelAlpha; }
            set
            {
                if (!Set(ref _panelAlpha, value, "PanelAlpha")) return;
                Raise("PanelBackground");
                Raise("OpacityText");
            }
        }
        public double? OpacityPreview { get; set; }
        public string OpacityText { get { return Math.Round(_panelAlpha * 100) + "%"; } }
        public int OpacityPercent
        {
            get { return (int)Math.Round(_panelAlpha * 100); }
            set { PanelAlpha = Math.Max(0.30, Math.Min(1.0, value / 100.0)); }
        }
        /// <summary>
        /// 面板底色，随透明度设置变化。
        /// 注意：条目卡片始终不透明（设计原则第 5 条），只有面板底色用这个。
        /// </summary>
        public Avalonia.Media.IBrush PanelBackground
        {
            get
            {
                return new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(
                    (byte)Math.Round(_panelAlpha * 255), 255, 255, 255));
            }
        }

        double _fadeStartDays = 7;        public double FadeStartDays
        {
            get { return _fadeStartDays; }
            set
            {
                if (!Set(ref _fadeStartDays, value, "FadeStartDays")) return;
                Raise("FadeText");
                // 保护期超过放手期时把放手期顶上去（和 panel.js 一致）
                if (BinAfterDays <= value) BinAfterDays = value + 1;
            }
        }
        public string FadeText { get { return (long)Math.Round(_fadeStartDays) + " 天"; } }

        double _binAfterDays = 21;
        public double BinAfterDays
        {
            get { return _binAfterDays; }
            set { if (Set(ref _binAfterDays, value, "BinAfterDays")) Raise("BinText"); }
        }
        public string BinText { get { return (long)Math.Round(_binAfterDays) + " 天"; } }

        /// <summary>滑块松手才写盘 —— 和 panel.js 的 input/change 分工一致。</summary>
        public void SaveDecay()
        {
            double fade = Math.Round(_fadeStartDays);
            double bin = Math.Round(Math.Max(_binAfterDays, fade + 1));
            var d = new Dictionary<string, object>
            {
                { "fadeStartDays", fade },
                { "fadeEndDays", bin },
                { "binAfterDays", bin },
            };
            _store.SetSetting("decay", d);
            Refresh();
        }

        public void SaveOpacity()
        {
            double v = Math.Round(_panelAlpha * 100) / 100.0;
            _store.SetSetting("panelOpacity", v);
        }

        public void ToggleDemo()
        {
            _store.SetSetting("demoFastDecay", !_demoOn);
            Refresh();
        }

        void ToggleQuick()
        {
            QuickOpen = !QuickOpen;
            if (QuickOpen) QuickFocusRequested();
        }

        public event Action QuickFocusRequested;

        void AddManual()
        {
            string v = (_quickText ?? "").Trim();
            if (v.Length == 0) return;
            _store.Add(_quickRoom, v, "", null, null);
            QuickText = "";
            Refresh();
        }

        void Open(string id)
        {
            var it = _store.Get(id);
            if (it == null) return;
            string room = J.Str(it, "room", "");
            string state = J.Str(it, "state", "");

            // 已完成的 Todo 再点一下 → 收进完成列表（HTML 版这里有个 0.3s 的消散动画）
            if (room == "todo" && state == "done")
            {
                _store.File_(id);
                Refresh();
                return;
            }
            // 磁贴上写着「点一下 = 看它一眼 = 续命」，所以 Wantdo 点开就真的续命
            if (room == "want") { _store.Touch(id); Refresh(); }

            if (OpenDetailRequested != null) OpenDetailRequested(id);
        }

        // ---------------- 数据刷新 ----------------

        public void Refresh()
        {
            long t0 = Program.Boot.ElapsedMilliseconds;
            var snap = _store.Snapshot();
            long t1 = Program.Boot.ElapsedMilliseconds;
            Raw = snap;

            var settings = J.Obj(J.Get(snap, "settings"));
            DemoOn = J.Bool(settings, "demoFastDecay", false);
            bool qc = J.Bool(settings, "quickCapture", false);
            if (qc != _quickCapture) { _quickCapture = qc; Raise("QuickCapture"); }
            bool aot = J.Bool(settings, "alwaysOnTop", true);
            if (aot != _alwaysOnTop) { _alwaysOnTop = aot; Raise("AlwaysOnTop"); }
            bool settingsDirty = false;
            if (OpacityPreview == null)
            {
                double a = J.Num(settings, "panelOpacity", 0.8);
                if (Math.Abs(a - _panelAlpha) > 0.0001) { _panelAlpha = a; settingsDirty = true; }
            }
            var cfg = J.Obj(J.Get(snap, "decayCfg"));
            if (cfg != null)
            {
                double f = J.Num(cfg, "fadeStartDays", 7);
                double b = J.Num(cfg, "binAfterDays", 21);
                if (Math.Abs(f - _fadeStartDays) > 0.0001) { _fadeStartDays = f; settingsDirty = true; }
                if (Math.Abs(b - _binAfterDays) > 0.0001) { _binAfterDays = b; settingsDirty = true; }
            }
            if (settingsDirty)
            {
                Raise("PanelAlpha"); Raise("PanelBackground"); Raise("OpacityText"); Raise("OpacityPercent");
                Raise("FadeStartDays"); Raise("FadeText"); Raise("BinAfterDays"); Raise("BinText");
            }

            var items = J.Arr(J.Get(snap, "items"));

            // --- Todo：排除纸篓，也排除「已完成并收走」的 ---
            var todo = new List<Dictionary<string, object>>();
            foreach (var o in items)
            {
                var it = J.Obj(o);
                if (J.Str(it, "room", "") != "todo") continue;
                if (J.Str(it, "state", "") == "binned") continue;
                if (!J.IsNull(J.Get(it, "filedAt"))) continue;
                todo.Add(it);
            }
            todo.Sort((a, b) =>
            {
                int sa = J.Str(a, "state", "") == "done" ? 1 : 0;
                int sb = J.Str(b, "state", "") == "done" ? 1 : 0;
                if (sa != sb) return sa - sb;
                int ra = Rank(a), rb = Rank(b);
                if (ra != rb) return ra - rb;
                return J.Num(a, "createdAt", 0).CompareTo(J.Num(b, "createdAt", 0));
            });

            // --- Wantdo ---
            var want = new List<Dictionary<string, object>>();
            foreach (var o in items)
            {
                var it = J.Obj(o);
                if (J.Str(it, "room", "") == "want" && J.Str(it, "state", "") == "active") want.Add(it);
            }
            want.Sort((a, b) => J.Num(b, "lastTouchedAt", 0).CompareTo(J.Num(a, "lastTouchedAt", 0)));

            // --- 纸篓 ---
            var bin = new List<Dictionary<string, object>>();
            foreach (var o in items)
            {
                var it = J.Obj(o);
                if (J.Str(it, "state", "") == "binned") bin.Add(it);
            }
            bin.Sort((a, b) => J.Num(b, "binnedAt", 0).CompareTo(J.Num(a, "binnedAt", 0)));

            // --- 完成列表：只有已收走的才进来 ---
            var done = new List<Dictionary<string, object>>();
            foreach (var o in items)
            {
                var it = J.Obj(o);
                if (J.Str(it, "state", "") != "done") continue;
                if (J.IsNull(J.Get(it, "doneAt")) || J.IsNull(J.Get(it, "filedAt"))) continue;
                done.Add(it);
            }
            done.Sort((a, b) => J.Num(b, "doneAt", 0).CompareTo(J.Num(a, "doneAt", 0)));

            Todos.Clear();
            foreach (var it in todo) Todos.Add(MakeTodo(it));
            long t2 = Program.Boot.ElapsedMilliseconds;
            Wants.Clear();
            foreach (var it in want) Wants.Add(MakeWant(it));
            Bins.Clear();
            foreach (var it in bin) Bins.Add(MakeBin(it));
            long t3 = Program.Boot.ElapsedMilliseconds;
            Program.Log(string.Format("  [t={0}ms]   · Refresh: Snapshot={1}ms Todo={2}ms Want+Bin={3}ms",
                t3, t1 - t0, t2 - t1, t3 - t2));

            DoneGroups.Clear();
            string lastKey = null;
            DoneGroupVm g = null;
            foreach (var it in done)
            {
                double doneAt = J.Num(it, "doneAt", 0);
                string k = Fmt.DayKey(doneAt);
                if (k != lastKey)
                {
                    g = new DoneGroupVm { Label = Fmt.DayLabel(doneAt), Items = new ObservableCollection<DoneVm>() };
                    DoneGroups.Add(g);
                    lastKey = k;
                }
                g.Items.Add(MakeDone(it));
                g.Count = g.Items.Count;
            }

            NTodo = CountActive(todo);
            NWant = want.Count;
            NBin = bin.Count;
            NDone = done.Count;

            var stats = J.Obj(J.Get(snap, "stats"));
            Stats = "本周完成 " + (long)J.Num(stats, "doneThisWeek", 0) + " 件 · 纸篓里 " + bin.Count + " 个想法";
            ShowSeedRow = items.Count == 0;

            Raise("HasTodos"); Raise("HasWants"); Raise("HasBins"); Raise("HasDoneGroups");
        }

        static int CountActive(List<Dictionary<string, object>> list)
        {
            int n = 0;
            foreach (var it in list) if (J.Str(it, "state", "") == "active") n++;
            return n;
        }

        static int Rank(Dictionary<string, object> x)
        {
            string b = J.Str(x, "dueBucket", null);
            if (b == "today") return 0;
            if (b == "soon") return 1;
            return 2;
        }

        string ImgPath(Dictionary<string, object> it)
        {
            string rel = J.Str(it, "image", null);
            if (string.IsNullOrEmpty(rel)) return null;
            return Path.Combine(_store.DataDir, rel.Replace('/', Path.DirectorySeparatorChar));
        }

        TodoVm MakeTodo(Dictionary<string, object> it)
        {
            bool isDone = J.Str(it, "state", "") == "done";
            string bucket = J.Str(it, "dueBucket", null);
            bool overdue = J.Bool(it, "overdue", false);
            string pill = null, kind = null;
            if (bucket == "today") { pill = overdue ? "逾期" : "Today"; kind = overdue ? "bad" : "today"; }
            else if (bucket == "soon") { pill = overdue ? "逾期" : "Soon"; kind = overdue ? "bad" : "soon"; }

            var vm = new TodoVm
            {
                Id = J.Str(it, "id", ""),
                Title = J.Str(it, "title", ""),
                IsDone = isDone,
                Overdue = overdue && !isDone,
                PillText = pill,
                PillKind = kind,
                MetaSource = J.Str(it, "source", ""),
                MetaTime = Fmt.RelTime(J.Num(it, "createdAt", Store.Now())),
                ImagePath = ImgPath(it),
            };
            QueueThumb(vm, ImgPath(it), 120);
            return vm;
        }

        WantVm MakeWant(Dictionary<string, object> it)
        {
            string title = J.Str(it, "title", "").Trim();
            string src = J.Str(it, "source", "").Trim();
            string cap = (src.Length > 0 && title.Length > 0) ? src + " · " + title
                       : (src.Length > 0 ? src : (title.Length > 0 ? title : Fmt.RelTime(J.Num(it, "createdAt", Store.Now()))));
            double life = J.Num(it, "life", 1);
            double left = J.Num(it, "daysLeft", 0);
            var vm = new WantVm
            {
                Id = J.Str(it, "id", ""),
                Title = title,
                Caption = cap,
                Thumb = null,   // 由下面的 QueueThumb 后台解码回填，绝不在这里同步解
                ImagePath = ImgPath(it),
                FirstChar = FirstCharOf(title),
                Opacity = J.Num(it, "opacity", 1),
                LifePercent = Math.Max(0, Math.Min(100, life * 100)),
                FadingHint = (life <= 0.32) ? "⏳ 还能撑 " + left.ToString("0.0", CultureInfo.InvariantCulture) + " 天" : null,
            };
            QueueThumb(vm, ImgPath(it), 220);
            return vm;
        }

        BinVm MakeBin(Dictionary<string, object> it)
        {
            string title = J.Str(it, "title", "").Trim();
            string src = J.Str(it, "source", "").Trim();
            string cap = (src.Length > 0 && title.Length > 0) ? src + " · " + title
                       : (src.Length > 0 ? src : (title.Length > 0 ? title : Fmt.RelTime(J.Num(it, "createdAt", Store.Now()))));
            bool isWant = J.Str(it, "room", "") == "want";
            var vm = new BinVm
            {
                Id = J.Str(it, "id", ""),
                Caption = cap,
                RoomName = isWant ? "Wantdo" : "Todo",
                ChipKind = isWant ? "want" : "todo",
                Thumb = null,
                ImagePath = ImgPath(it),
                FirstChar = FirstCharOf(title),
            };
            QueueThumb(vm, ImgPath(it), 220, true);   // 纸篓里的图去色，和 CSS 的 grayscale 呼应
            return vm;
        }

        DoneVm MakeDone(Dictionary<string, object> it)
        {
            string title = J.Str(it, "title", "").Trim();
            string src = J.Str(it, "source", "").Trim();
            bool isWant = J.Str(it, "room", "") == "want";
            double doneAt = J.Num(it, "doneAt", 0);
            var t = DateTimeOffset.FromUnixTimeMilliseconds((long)doneAt).ToLocalTime();
            string hhmm = t.ToString("HH:mm");

            var vm = new DoneVm
            {
                Id = J.Str(it, "id", ""),
                Title = title.Length > 0 ? title : (src.Length > 0 ? src : "（没有标题）"),
                RoomName = isWant ? "Wantdo" : "Todo",
                ChipKind = isWant ? "want" : "todo",
                MetaText = "用时 " + Fmt.Dur(J.Num(it, "elapsedMs", 0)) + " · " + hhmm + " 完成",
                Thumb = null,
                ImagePath = ImgPath(it),
                FirstChar = FirstCharOf(title.Length > 0 ? title : src),
            };
            QueueThumb(vm, ImgPath(it), 120);
            return vm;
        }

        /// <summary>
        /// 缩略图一律后台解码再回填。启动时同步解码是原生版最大的一处卡顿来源
        /// （11 张图要把首帧推后 700 多毫秒）。
        ///
        /// 另外会顺手写一份 <c>&lt;id&gt;.thumb.png</c> 到磁盘：WPF 的 PNG 解码不做缩放解码，
        /// 一张 2560×1440 的截图哪怕只要 120px 宽也得整张解一遍。有了缩略图，第二次启动就只读小图。
        /// 老数据没有缩略图也没关系 —— 第一次仍然慢，但会当场补上。
        /// </summary>
        const int ThumbWidth = 256;

        void QueueThumb(object vm, string imagePath, int decodeWidth, bool gray = false)
        {
            if (Program.NoThumb) return;   // A/B 用：彻底跳过解码，隔离瓶颈
            if (string.IsNullOrEmpty(imagePath)) return;
            string thumb = ThumbPathOf(imagePath);

            if (File.Exists(thumb))
            {
                Images.GetAsync(thumb, decodeWidth, img => ApplyThumb(vm, gray ? ToGray(img) : img));
                return;
            }
            Images.GetAsync(imagePath, ThumbWidth, img =>
            {
                ApplyThumb(vm, gray ? ToGray(img) : img);
                TryWriteThumb(img, thumb);
            });
        }

        /// <summary>
        /// 纸篓卡片的去色。panel.css 用的是 <c>filter: grayscale(0.7)</c>。
        ///
        /// WPF 版靠 FormatConvertedBitmap 转 Gray8 格式；Avalonia 没有等价的格式转换，
        /// 所以这里直接把像素读出来按亮度公式改一遍再写回。
        /// 只对缩略图做（几百像素宽），开销可忽略，不影响面板启动。
        /// </summary>
        static Bitmap ToGray(Bitmap src)
        {
            if (src == null) return null;
            try
            {
                var size = src.PixelSize;
                int w = size.Width, h = size.Height;
                if (w <= 0 || h <= 0) return src;

                int stride = w * 4;
                var buf = new byte[stride * h];
                var handle = System.Runtime.InteropServices.GCHandle.Alloc(
                    buf, System.Runtime.InteropServices.GCHandleType.Pinned);
                try
                {
                    // 显式写全限定名：本文件同时 using 了 Avalonia 和我们的平台层，
                    // 而平台层里也有一个矩形类型。写清楚是哪一个，别靠编译器猜。
                    src.CopyPixels(new Avalonia.PixelRect(0, 0, w, h),
                        handle.AddrOfPinnedObject(), buf.Length, stride);
                }
                finally { handle.Free(); }

                // 按 luminance 去色，保留 alpha
                for (int i = 0; i + 3 < buf.Length; i += 4)
                {
                    byte b = buf[i], g = buf[i + 1], r = buf[i + 2];
                    byte y = (byte)((r * 299 + g * 587 + b * 114) / 1000);
                    buf[i] = y; buf[i + 1] = y; buf[i + 2] = y;
                }

                var wb = new WriteableBitmap(size, new Vector(96, 96),
                    PixelFormat.Bgra8888, AlphaFormat.Premul);
                using (var fb = wb.Lock())
                {
                    unsafe
                    {
                        var dstBase = (byte*)fb.Address;
                        for (int y = 0; y < h; y++)
                        {
                            System.Runtime.InteropServices.Marshal.Copy(
                                buf, y * stride, (IntPtr)(dstBase + y * fb.RowBytes), stride);
                        }
                    }
                }
                return wb;
            }
            catch (Exception) { return src; }   // 转不动就退回彩色，不能让卡片变空
        }

        public static string ThumbPathOf(string imagePath)
        {
            if (string.IsNullOrEmpty(imagePath)) return null;
            string dir = Path.GetDirectoryName(imagePath);
            string name = Path.GetFileNameWithoutExtension(imagePath);
            return Path.Combine(dir, name + ".thumb.png");
        }

        static void TryWriteThumb(Bitmap img, string thumbPath)
        {
            try
            {
                if (img == null || string.IsNullOrEmpty(thumbPath)) return;
                string tmp = thumbPath + ".tmp";
                // 先写临时文件再改名：避免写到一半崩溃留下半个 PNG，
                // 下次启动会把它当成有效缩略图读进来（原版同一个考虑）。
                using (var fs = File.Create(tmp))
                    img.Save(fs, new PngBitmapEncoderOptions());
                if (File.Exists(thumbPath)) File.Delete(thumbPath);
                File.Move(tmp, thumbPath);
            }
            catch (Exception) { }
        }

        /// <summary>把解好的图回填到对应条目，并通知界面重画。</summary>
        void ApplyThumb(object vm, Bitmap img)
        {
            bool hit = false;
            var a = vm as TodoVm; if (a != null) { a.Thumb = img; hit = true; }
            var b = vm as WantVm; if (b != null) { b.Thumb = img; hit = true; }
            var c = vm as BinVm; if (c != null) { c.Thumb = img; hit = true; }
            var d = vm as DoneVm; if (d != null) { d.Thumb = img; hit = true; }
            if (!hit) return;

            var h = ImageRefreshNeeded;
            if (h != null) h();
        }

        static string FirstCharOf(string s)
        {
            s = (s ?? "").Trim();
            if (s.Length == 0) return "·";
            var e = StringInfo.GetTextElementEnumerator(s);
            return e.MoveNext() ? e.GetTextElement() : "·";
        }
    }
}
