using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace RemeberEr
{
    /// <summary>
    /// 详情窗「看它一眼」。照着 src/renderer/detail/ 搬：
    /// 打字 600ms 防抖自动保存、失焦立即保存、关窗前兜底保存、Esc / Ctrl+Enter 保存并关闭。
    /// Wantdo 点 Get Done 先去写心得，Todo 直接完成并收走。
    /// </summary>
    public partial class DetailWindow : Window
    {
        // 和 main.js 里的常量一致
        const int DETAIL_CHROME_H = 288, DETAIL_CHROME_W = 32;
        const int DETAIL_MAX_W = 900, DETAIL_MAX_H = 840;
        const int DETAIL_MIN_W = 420, DETAIL_MIN_H = 340;

        readonly Store _store;
        readonly string _id;
        /// <summary>这一扇窗开的是哪一条。详情窗能同时开好几扇，靠它去重。</summary>
        public string ItemId { get { return _id; } }

        /// <summary>点了「补一张截图」—— 交给 PanelWindow 去走框选流程。</summary>
        public event Action<string> ScreenshotRequested;

        void OnAddShot(object sender, RoutedEventArgs e)
        {
            var h = ScreenshotRequested;
            if (h != null) h(_id);
        }
        Dictionary<string, object> _item;

        DispatcherTimer _saveTimer;
        DispatcherTimer _savedFlash;
        /// <summary>LoadItem 期间会被字段的 TextChanged 打到，别让它误触发自动保存和「已保存」闪烁。</summary>
        bool _loading;

        /// <summary>Wantdo 完成后要写心得，交给主窗口去开记录窗。</summary>
        public event Action<string> RecordRequested;

        public DetailWindow(Store store, string itemId)
        {
            InitializeComponent();
            _store = store;
            _id = itemId;

            var size = SizeFor(itemId);
            Width = size.Width;
            Height = size.Height;

            Loaded += (s, e) => { LoadItem(); TitleField.Focus(); };
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            Interop.HideFromAltTab(new WindowInteropHelper(this).Handle);
            // 居中在工作区
            var wa = SystemParameters.WorkArea;
            Left = wa.Left + (wa.Width - Width) / 2;
            Top = wa.Top + (wa.Height - Height) / 2;
        }

        /// <summary>按图片实际尺寸算窗口大小 —— 和 main.js 的 detailSizeFor 同一套算法。</summary>
        Size SizeFor(string id)
        {
            double iw = 640, ih = 420;
            var it = _store.Get(id);
            string rel = it != null ? J.Str(it, "image", null) : null;
            if (!string.IsNullOrEmpty(rel))
            {
                string abs = Path.Combine(_store.DataDir, rel.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(abs))
                {
                    try
                    {
                        var b = new BitmapImage();
                        b.BeginInit();
                        b.CacheOption = BitmapCacheOption.OnLoad;
                        b.UriSource = new Uri(abs, UriKind.Absolute);
                        b.EndInit();
                        if (b.PixelWidth > 0 && b.PixelHeight > 0) { iw = b.PixelWidth; ih = b.PixelHeight; }
                    }
                    catch (Exception) { }
                }
            }

            double availW = DETAIL_MAX_W - DETAIL_CHROME_W;
            double availH = DETAIL_MAX_H - DETAIL_CHROME_H;
            double scale = Math.Min(1, Math.Min(availW / iw, availH / ih));

            return new Size(
                Math.Max(DETAIL_MIN_W, Math.Round(iw * scale) + DETAIL_CHROME_W),
                Math.Max(DETAIL_MIN_H, Math.Round(ih * scale) + DETAIL_CHROME_H));
        }

        /// <summary>
        /// 让这扇窗重新读一遍条目。
        /// 补截图之后要叫它 —— 否则图挂上去了，窗里还杵着那个「补一张截图」的加号。
        /// </summary>
        public void Reload() { LoadItem(); }

        void LoadItem()
        {
            _loading = true;
            // 先巡检一次：过期的 Wantdo 该进纸篓就进，别让详情窗显示一个已经该放手的条目还不自知
            _store.Sweep();
            _item = _store.Get(_id);
            if (_item == null) { _loading = false; Close(); return; }

            bool isWant = J.Str(_item, "room", "") == "want";
            TagText.Text = isWant ? "Wantdo" : "Todo";
            string src = J.Str(_item, "source", "");
            SrcText.Text = src.Length > 0 ? src : "未知";
            WhenText.Text = FmtStamp(J.Num(_item, "createdAt", 0));

            // 没有标题就是空的 —— 不写「（没有标题）」那种占位文字
            TitleField.Text = J.Str(_item, "title", "");
            NoteField.Text = J.Str(_item, "note", "");

            if (isWant)
            {
                LifeRow.Visibility = Visibility.Visible;
                // 注意：life / opacity 这些衰减字段只存在于 snapshot 里，原始条目没有。
                // 直接从原始条目取 life 会永远拿到 0 —— 所以这里按同一套 Decay 现算。
                var cfg = Decay.CfgFor(_store.Settings);
                double scale = Decay.TimeScaleFor(_store.Settings);
                double life = Decay.LifeFor(_item, Store.Now(), cfg, scale, Decay.DemoStartedAtFor(_store.Settings));
                int pct = (int)Math.Round(Math.Min(1, Math.Max(0, life)) * 100);
                LifeText.Text = pct + "%　（被碰过 " + (long)J.Num(_item, "touchCount", 0) + " 次）";
            }
            else
            {
                LifeRow.Visibility = Visibility.Collapsed;
            }

            LoadDdl(isWant);   // Todo 多一行可编辑的 DDL

            bool done = J.Str(_item, "state", "") == "done";
            GetDoneBtn.Visibility = done ? Visibility.Collapsed : Visibility.Visible;
            DoneHint.Text = done ? "这条已经完成了" : (isWant ? "完成时会让你写一句心得" : "");

            string rel = J.Str(_item, "image", null);
            if (!string.IsNullOrEmpty(rel))
            {
                string abs = Path.Combine(_store.DataDir, rel.Replace('/', Path.DirectorySeparatorChar));
                Pic.Source = Images.Get(abs, 900);
            }
            bool hasImg = Pic.Source != null;
            Pic.Visibility = hasImg ? Visibility.Visible : Visibility.Collapsed;
            // 没图 -> 中间摆一个大的「补一张截图」；有图 -> 右下角一个小加号（换图）
            AddShot.Visibility = hasImg ? Visibility.Collapsed : Visibility.Visible;
            AddShotSmall.Visibility = hasImg ? Visibility.Visible : Visibility.Collapsed;

            _loading = false;
        }

        static string FmtStamp(double ts)
        {
            if (ts <= 0) return "—";
            return DateTimeOffset.FromUnixTimeMilliseconds((long)ts).ToLocalTime()
                .ToString("yyyy-MM-dd HH:mm");
        }

        // ============================================================ 保存

        /// <summary>打字时防抖保存，不用手动点保存。</summary>
        void OnFieldChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (_item == null || _loading) return;
            if (_saveTimer == null)
            {
                _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
                _saveTimer.Tick += (s, a) => { _saveTimer.Stop(); SaveNow(); };
            }
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        void OnFieldBlur(object sender, RoutedEventArgs e) { SaveNow(); }

        void SaveNow()
        {
            if (_item == null) return;
            if (_saveTimer != null) _saveTimer.Stop();

            var patch = new Dictionary<string, object>
            {
                { "title", TitleField.Text ?? "" },
                { "note", NoteField.Text ?? "" },
            };
            _store.Update(_id, patch);
            _item = _store.Get(_id);
            FlashSaved();
        }

        void FlashSaved()
        {
            SavedText.Opacity = 1;
            if (_savedFlash == null)
            {
                _savedFlash = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
                _savedFlash.Tick += (s, a) => { _savedFlash.Stop(); SavedText.Opacity = 0; };
            }
            _savedFlash.Stop();
            _savedFlash.Start();
        }

        // ============================================================ 交互

        void OnHeaderDrag(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;
            try { DragMove(); } catch (Exception) { }
        }

        void OnClose(object sender, RoutedEventArgs e) { SaveNow(); Close(); }

        // ---------------- DDL 编辑 ----------------
        //
        // 显示 `xx天 xx小时`，点一下摊成上下两个转轮，天 / 小时各以 1 为步长。
        // 想让一条已经建好的 Todo 改期限，不用再去截图重来。

        long _ddlDays, _ddlHours;

        void LoadDdl(bool isWant)
        {
            // Wantdo 没有期限，这一行不出现
            DdlRow.Visibility = isWant ? Visibility.Collapsed : Visibility.Visible;
            if (isWant) return;

            object da = J.Get(_item, "dueAt");
            long now = Store.Now();
            long left = da is double ? (long)(double)da - now : 0;
            if (left < 0) left = 0;
            _ddlDays = left / 86400000L;
            _ddlHours = (left % 86400000L) / 3600000L;

            ShowDdlChip();
        }

        void ShowDdlChip()
        {
            DdlChip.Visibility = Visibility.Visible;
            DdlSpin.Visibility = Visibility.Collapsed;
            DdlText.Text = DdlPhrase();
        }

        void ShowDdlSpin()
        {
            DdlChip.Visibility = Visibility.Collapsed;
            DdlSpin.Visibility = Visibility.Visible;
            RefreshDdlSpin();
        }

        void RefreshDdlSpin()
        {
            DayVal.Text = _ddlDays.ToString();
            HourVal.Text = _ddlHours.ToString();
        }

        string DdlPhrase()
        {
            object da = J.Get(_item, "dueAt");
            if (!(da is double)) return "还没设期限 · 点一下加上";

            // 逾期要说「已经逾期多久」—— 这时候 _ddlDays/_ddlHours 是 0
            //（转轮从 0 起，只能往后推），不能拿它们来造句。
            long diff = (long)(double)da - Store.Now();
            if (diff < 0)
            {
                long od = -diff;
                return "已逾期 " + (od / 86400000L) + " 天 " + ((od % 86400000L) / 3600000L) + " 小时";
            }
            if (_ddlDays <= 0) return _ddlHours + " 小时后";
            return _ddlDays + " 天 " + _ddlHours + " 小时";
        }

        void OnDdlChipClick(object sender, MouseButtonEventArgs e)
        {
            if (e != null) e.Handled = true;   // 验收钩子会传 null
            ShowDdlSpin();
        }

        void OnDdlDone(object sender, RoutedEventArgs e) { ShowDdlChip(); }

        void OnDayUp(object sender, RoutedEventArgs e) { _ddlDays++; RefreshDdlSpin(); ApplyDdl(); }
        void OnDayDown(object sender, RoutedEventArgs e) { if (_ddlDays > 0) _ddlDays--; RefreshDdlSpin(); ApplyDdl(); }
        void OnHourUp(object sender, RoutedEventArgs e) { if (_ddlHours < 23) _ddlHours++; RefreshDdlSpin(); ApplyDdl(); }
        void OnHourDown(object sender, RoutedEventArgs e) { if (_ddlHours > 0) _ddlHours--; RefreshDdlSpin(); ApplyDdl(); }

        // 验收用的钩子：直接走一遍点击逻辑，不用真去点
        public void SimDdlOpen() { OnDdlChipClick(null, null); }
        public void SimDdlDone() { OnDdlDone(null, null); }
        public void SimDay(int d) { for (int i = 0; i < Math.Abs(d); i++) { if (d > 0) OnDayUp(null, null); else OnDayDown(null, null); } }
        public void SimHour(int d) { for (int i = 0; i < Math.Abs(d); i++) { if (d > 0) OnHourUp(null, null); else OnHourDown(null, null); } }
        public string DdlState()
        {
            return string.Format("days={0} hours={1} chip=\"{2}\" spin可见={3}",
                _ddlDays, _ddlHours, DdlText.Text, DdlSpin.Visibility == Visibility.Visible);
        }

        /// <summary>把「还剩 N 天 M 小时」落成绝对时间戳。</summary>
        void ApplyDdl()
        {
            long due = Store.Now() + _ddlDays * 86400000L + _ddlHours * 3600000L;
            var patch = new Dictionary<string, object>
            {
                { "dueAt", (double)due },
                { "dueDays", (double)_ddlDays },
            };
            // 原来没设过期限的，给个档位，卡片上才会出药丸
            string bucket = J.Str(_item, "dueBucket", null);
            if (bucket == null) bucket = "soon";

            _store.SetDue(_id, (double)due, (int)_ddlDays, bucket);
            _item = _store.Get(_id);
            DdlText.Text = DdlPhrase();
            Program.Log(string.Format("  DDL 改成 {0} 天 {1} 小时 -> {2}",
                _ddlDays, _ddlHours,
                DateTimeOffset.FromUnixTimeMilliseconds(due).ToLocalTime().ToString("yyyy-MM-dd HH:mm")));
        }

        void OnGetDone(object sender, RoutedEventArgs e)
        {
            SaveNow();
            bool isWant = _item != null && J.Str(_item, "room", "") == "want";

            if (isWant)
            {
                // 先写心得，再算完成
                var r = RecordRequested;
                if (r != null) r(_id);
                return;
            }

            // Todo：直接完成并收走
            _store.Done(_id, false, null, true);
            Close();
        }

        void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                SaveNow();
                Close();
                e.Handled = true;
                return;
            }
            // Ctrl+Enter 也当保存并关闭
            if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                SaveNow();
                Close();
                e.Handled = true;
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            SaveNow();   // 关窗前兜底
            base.OnClosed(e);
        }
    }
}
