using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RemeberEr
{
    /// <summary>
    /// 缩略图缓存。DecodePixelWidth 是关键 —— 一张 2560×1440 的 PNG 原样解码要十几 MB，
    /// 而卡片上只显示 46×34。按目标宽度解码能把常驻内存压下来一个量级。
    ///
    /// 另外解码一律走 GetAsync：PNG 解码是纯 CPU 的，放在 UI 线程上会把启动时间直接拖垮
    /// （实测 11 张图同步解码要 775ms）。
    /// </summary>
    public static class Images
    {
        static readonly Dictionary<string, ImageSource> _cache = new Dictionary<string, ImageSource>();
        static readonly object _gate = new object();

        static bool TryCached(string key, out ImageSource hit)
        {
            lock (_gate) { return _cache.TryGetValue(key, out hit); }
        }

        static void Store(string key, ImageSource img)
        {
            lock (_gate) { _cache[key] = img; }
        }

        /// <summary>同步取图（已经缓存时便宜；没缓存则阻塞解码）。</summary>
        public static ImageSource Get(string absolutePath, int decodeWidth)
        {
            if (string.IsNullOrEmpty(absolutePath) || !File.Exists(absolutePath)) return null;
            string key = absolutePath + "|" + decodeWidth;
            ImageSource hit;
            if (TryCached(key, out hit)) return hit;

            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;   // 立刻读进内存，别锁文件
                bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                if (decodeWidth > 0) bmp.DecodePixelWidth = decodeWidth;
                bmp.UriSource = new Uri(absolutePath, UriKind.Absolute);
                bmp.EndInit();
                bmp.Freeze();                                  // 冻结后可以跨线程共享
                Store(key, bmp);
                return bmp;
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
        /// （实测并发解码时首帧 1015ms，串行低优先级之后回到 500ms 量级）。
        /// </summary>
        public static void GetAsync(string absolutePath, int decodeWidth, Action<ImageSource> done)
        {
            if (string.IsNullOrEmpty(absolutePath) || !File.Exists(absolutePath))
            {
                if (done != null) done(null);
                return;
            }
            string key = absolutePath + "|" + decodeWidth;
            ImageSource hit;
            if (TryCached(key, out hit)) { if (done != null) done(hit); return; }

            Enqueue(() =>
            {
                var img = Get(absolutePath, decodeWidth);
                if (done == null) return;
                var app = System.Windows.Application.Current;
                if (app == null) return;
                app.Dispatcher.BeginInvoke(new Action(() => done(img)));
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
        ImageSource _thumb;
        public ImageSource Thumb
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
        ImageSource _thumb;
        public ImageSource Thumb
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
        public ImageSource BigImage { get { return Thumb; } }
    }

    public class BinVm : ObservableObject
    {
        public string Id { get; set; }
        public string Caption { get; set; }
        public string RoomName { get; set; }
        public string ChipKind { get; set; }
        ImageSource _thumb;
        public ImageSource Thumb
        {
            get { return _thumb; }
            set { _thumb = value; Raise("Thumb"); Raise("HasThumb"); }
        }
        public string ImagePath { get; set; }
        public bool HasThumb { get { return Thumb != null; } }
        public string FirstChar { get; set; }

        /// <summary>
        /// 能不能点一下捡回来。
        /// 使命 Todo 不行 —— 舍弃就是舍弃，只留一个「倒垃圾」一次清空；
        /// 初心 Wantdo 可以，而且永远不会被删。
        /// </summary>
        public bool CanRestore { get; set; }
    }

    public class DoneVm : ObservableObject
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string RoomName { get; set; }
        public string ChipKind { get; set; }
        public string MetaText { get; set; }
        ImageSource _thumb;
        public ImageSource Thumb
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
        /// <summary>纸篓的两个分区：使命 Todo（可全清，不能捡回）/ 初心 Wantdo（能捡回，永不删）</summary>
        public ObservableCollection<BinVm> TodoBins { get; private set; }
        public ObservableCollection<BinVm> WantBins { get; private set; }
        public ObservableCollection<DoneGroupVm> DoneGroups { get; private set; }
        /// <summary>完成列表也按房间分两个标签：Todo / WD</summary>
        public ObservableCollection<DoneGroupVm> TodoDoneGroups { get; private set; }
        public ObservableCollection<DoneGroupVm> WantDoneGroups { get; private set; }

        public Dictionary<string, object> Raw { get; private set; }

        public PanelViewModel(Store store)
        {
            _store = store;
            Todos = new ObservableCollection<TodoVm>();
            Wants = new ObservableCollection<WantVm>();
            Bins = new ObservableCollection<BinVm>();
            TodoBins = new ObservableCollection<BinVm>();
            WantBins = new ObservableCollection<BinVm>();
            DoneGroups = new ObservableCollection<DoneGroupVm>();
            TodoDoneGroups = new ObservableCollection<DoneGroupVm>();
            WantDoneGroups = new ObservableCollection<DoneGroupVm>();

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
            EmptyTrashCmd = new RelayCommand(EmptyTrash);
            BinTabCmd = new RelayCommand(p => SwitchBinTab((string)p));
            DoneTabCmd = new RelayCommand(p => SwitchDoneTab((string)p));
            OpenCmd = new RelayCommand(p => Open((string)p));
            OpenDoneCmd = new RelayCommand(p => { if (OpenRecordRequested != null) OpenRecordRequested((string)p); });
        }

        public event Action SnipRequested;
        public event Action CollapseRequested;
        public event Action QuitRequested;
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
        public RelayCommand EmptyTrashCmd { get; private set; }

        // ---------------- 倒垃圾 ----------------

        /// <summary>
        /// 「倒垃圾」是**整个程序里唯一不可逆的操作** —— 别的都能反悔，这个不能。
        /// 所以点第一下只把按钮变成「确定清空？」，点第二下才真清；4 秒不点自己缩回去。
        /// </summary>
        bool _confirmingEmpty;
        public bool ConfirmingEmpty
        {
            get { return _confirmingEmpty; }
            set
            {
                if (!Set(ref _confirmingEmpty, value, "ConfirmingEmpty")) return;
                Raise("EmptyTrashText");
            }
        }
        public string EmptyTrashText { get { return _confirmingEmpty ? "确定清空？" : "倒垃圾"; } }

        System.Windows.Threading.DispatcherTimer _emptyConfirmTimer;
        void EmptyTrash()
        {
            if (!_confirmingEmpty)
            {
                ConfirmingEmpty = true;
                if (_emptyConfirmTimer == null)
                {
                    _emptyConfirmTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
                    _emptyConfirmTimer.Tick += (s, e) =>
                    {
                        _emptyConfirmTimer.Stop();
                        ConfirmingEmpty = false;
                    };
                }
                _emptyConfirmTimer.Stop();
                _emptyConfirmTimer.Start();
                return;
            }

            if (_emptyConfirmTimer != null) _emptyConfirmTimer.Stop();
            ConfirmingEmpty = false;

            // 只清「使命 Todo」那一区；初心 Wantdo 永远不动
            var ids = new List<string>();
            foreach (var b in TodoBins) ids.Add(b.Id);
            foreach (var id in ids) _store.Remove(id);

            Program.Log("  倒垃圾：清掉 " + ids.Count + " 条使命 Todo（初心 Wantdo 未动）");
            Refresh();
        }
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
                ConfirmingEmpty = false;   // 离开这一页，「确定清空？」就作废
                AutoPickTab();
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
        public bool HasTodoBins { get { return TodoBins.Count > 0; } }
        public bool HasWantBins { get { return WantBins.Count > 0; } }
        /// <summary>按「今天 / 昨天 / 更早」把完成条目分组（已按 doneAt 倒序）。</summary>
        void BuildDoneGroups(List<Dictionary<string, object>> src, ObservableCollection<DoneGroupVm> target)
        {
            string lastKey = null;
            DoneGroupVm g = null;
            foreach (var it in src)
            {
                double doneAt = J.Num(it, "doneAt", 0);
                string k = Fmt.DayKey(doneAt);
                if (k != lastKey)
                {
                    g = new DoneGroupVm { Label = Fmt.DayLabel(doneAt), Items = new ObservableCollection<DoneVm>() };
                    target.Add(g);
                    lastKey = k;
                }
                g.Items.Add(MakeDone(it));
                g.Count = g.Items.Count;
            }
        }

        // ---------------- 标签页 ----------------
        //
        // 纸篓和完成列表都按房间分两个标签。纸篓那两个有名字（使命 / 初心），
        // 完成列表就用朴素的名字（Todo / WD）—— 完成了就是完成了，不用再抒情。

        string _binTab = "todo";
        public string BinTab
        {
            get { return _binTab; }
            set
            {
                if (!Set(ref _binTab, value, "BinTab")) return;
                Raise("IsBinTodo"); Raise("IsBinWant"); Raise("BinTodoTabTag"); Raise("BinWantTabTag");
            }
        }
        public bool IsBinTodo { get { return _binTab == "todo"; } }
        public bool IsBinWant { get { return _binTab == "want"; } }

        string _doneTab = "todo";
        public string DoneTab
        {
            get { return _doneTab; }
            set
            {
                if (!Set(ref _doneTab, value, "DoneTab")) return;
                Raise("IsDoneTodo"); Raise("IsDoneWant");
            }
        }
        public bool IsDoneTodo { get { return _doneTab == "todo"; } }
        public bool IsDoneWant { get { return _doneTab == "want"; } }

        public void SwitchBinTab(string t) { if (!string.IsNullOrEmpty(t)) BinTab = t; }
        public void SwitchDoneTab(string t) { if (!string.IsNullOrEmpty(t)) DoneTab = t; }

        public RelayCommand BinTabCmd { get; private set; }
        public RelayCommand DoneTabCmd { get; private set; }

        /// <summary>
        /// 进某个标签页视图时，如果首选那个标签是空的、另一个有东西，就自动切过去 ——
        /// 免得点进去看到的是一片空白，以为东西丢了。
        /// </summary>
        void AutoPickTab()
        {
            if (_view == "bin")
            {
                if (TodoBins.Count == 0 && WantBins.Count > 0) BinTab = "want";
                else if (WantBins.Count == 0 && TodoBins.Count > 0) BinTab = "todo";
            }
            else if (_view == "done")
            {
                if (TodoDoneGroups.Count == 0 && WantDoneGroups.Count > 0) DoneTab = "want";
                else if (WantDoneGroups.Count == 0 && TodoDoneGroups.Count > 0) DoneTab = "todo";
            }
        }

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
        public Brush PanelBackground
        {
            get
            {
                var b = new SolidColorBrush(Color.FromArgb(
                    (byte)Math.Round(_panelAlpha * 255), 255, 255, 255));
                b.Freeze();
                return b;
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
            TodoBins.Clear();
            WantBins.Clear();
            foreach (var it in bin)
            {
                var vm = MakeBin(it);
                Bins.Add(vm);
                // 按房间分到两个分区：使命 Todo 能全清但不能捡回，初心 Wantdo 能捡回但永不删
                if (J.Str(it, "room", "") == "todo") { vm.CanRestore = false; TodoBins.Add(vm); }
                else { vm.CanRestore = true; WantBins.Add(vm); }
            }
            long t3 = Program.Boot.ElapsedMilliseconds;
            Program.Log(string.Format("  [t={0}ms]   · Refresh: Snapshot={1}ms Todo={2}ms Want+Bin={3}ms",
                t3, t1 - t0, t2 - t1, t3 - t2));

            DoneGroups.Clear();
            TodoDoneGroups.Clear();
            WantDoneGroups.Clear();
            BuildDoneGroups(done, DoneGroups);
            BuildDoneGroups(done.FindAll(o => J.Str(o, "room", "") == "todo"), TodoDoneGroups);
            BuildDoneGroups(done.FindAll(o => J.Str(o, "room", "") != "todo"), WantDoneGroups);

            NTodo = CountActive(todo);
            NWant = want.Count;
            NBin = bin.Count;
            NDone = done.Count;

            var stats = J.Obj(J.Get(snap, "stats"));
            Stats = "本周完成 " + (long)J.Num(stats, "doneThisWeek", 0) + " 件 · 纸篓里 " + bin.Count + " 个想法";
            ShowSeedRow = items.Count == 0;

            Raise("HasTodos"); Raise("HasWants"); Raise("HasBins");
            Raise("HasTodoBins"); Raise("HasWantBins"); Raise("HasDoneGroups");
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

        static void QueueThumb(object vm, string imagePath, int decodeWidth, bool gray = false)
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
        /// 纸篓卡片的去色。panel.css 用的是 <c>filter: grayscale(0.7)</c>，
        /// WPF 不加像素着色器没有等价物，这里直接用 WPF 自带的格式转换转成 Gray8 —— 纯托管、零依赖。
        /// </summary>
        static ImageSource ToGray(ImageSource src)
        {
            var bmp = src as BitmapSource;
            if (bmp == null) return src;
            try
            {
                var gray = new FormatConvertedBitmap(bmp, PixelFormats.Gray8, null, 0);
                gray.Freeze();
                return gray;
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

        static void TryWriteThumb(ImageSource img, string thumbPath)
        {
            try
            {
                var frame = img as BitmapSource;
                if (frame == null || string.IsNullOrEmpty(thumbPath)) return;
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(frame));
                string tmp = thumbPath + ".tmp";
                using (var fs = File.Create(tmp)) enc.Save(fs);
                if (File.Exists(thumbPath)) File.Delete(thumbPath);
                File.Move(tmp, thumbPath);
            }
            catch (Exception) { }
        }

        static void ApplyThumb(object vm, ImageSource img)
        {
            var a = vm as TodoVm; if (a != null) { a.Thumb = img; return; }
            var b = vm as WantVm; if (b != null) { b.Thumb = img; return; }
            var c = vm as BinVm; if (c != null) { c.Thumb = img; return; }
            var d = vm as DoneVm; if (d != null) { d.Thumb = img; }
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
