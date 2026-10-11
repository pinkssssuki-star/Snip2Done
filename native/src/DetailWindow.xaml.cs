using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
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
            RefreshDdlNums();
        }

        void RefreshDdlNums()
        {
            DayNum.Text = _ddlDays.ToString();
            HourNum.Text = _ddlHours.ToString();
        }

        // ---------------- 转轮 ----------------
        //
        // 点数字 → 在**上层**弹一个 Popup（不参与布局，所以详情窗不会顶出两行）。
        // 滚轮或上下拖都能转，一格一格吸附；离中心越远的数字越小越淡。

        bool _drumIsDay;
        bool _drumDragging;
        double _drumLastY;
        const double ROW_H = 28;      // 一格的高度
        const int DRUM_ROWS = 5;      // 中心 ±2

        void OpenDrum(FrameworkElement anchor, bool isDay)
        {
            _drumIsDay = isDay;
            DrumPop.PlacementTarget = anchor;
            DrumPop.IsOpen = true;
            RebuildDrum();
        }

        /// <summary>
        /// 按当前值铺 7 行（中心 ±3）。铺得比视口高，才有东西可以滑进来。
        /// 上大下小 —— 和真转轮一样，往上转是更大的数。
        /// </summary>
        void RebuildDrum()
        {
            DrumStrip.Children.Clear();
            long v = _drumIsDay ? _ddlDays : _ddlHours;
            long max = _drumIsDay ? 999 : 23;

            for (int off = 3; off >= -3; off--)
            {
                long n = v + off;
                if (n < 0) n = 0;
                if (n > max) n = max;
                double d = Math.Abs(off);

                var t = new TextBlock
                {
                    Text = n.ToString(),
                    Height = ROW_H,
                    LineHeight = ROW_H,
                    TextAlignment = TextAlignment.Center,
                    FontSize = off == 0 ? 17 : 13,
                    FontWeight = off == 0 ? FontWeights.SemiBold : FontWeights.Normal,
                    Foreground = new SolidColorBrush(off == 0
                        ? Color.FromRgb(0x1c, 0x1c, 0x1e)
                        : Color.FromRgb(0x86, 0x86, 0x8c)),
                    Opacity = off == 0 ? 1.0 : Math.Max(0.12, 0.62 - d * 0.18),
                };
                if (off != 0)
                {
                    double sx = Math.Max(0.5, 0.86 - d * 0.10);
                    t.RenderTransformOrigin = new Point(0.5, 0.5);
                    t.RenderTransform = new ScaleTransform(sx, sx);
                }
                DrumStrip.Children.Add(t);
            }
            SetStripY(0);
        }

        void SetStripY(double y)
        {
            var tt = DrumStrip.RenderTransform as TranslateTransform;
            if (tt == null)
            {
                tt = new TranslateTransform(0, 0);
                DrumStrip.RenderTransform = tt;
            }
            tt.Y = y;
        }

        // ---------------- 滚动动画 ----------------
        //
        // 不是"重画一下就跳到新位置"，而是**把条带整体平移一格**，逐帧走完再重居中。
        // 重居中之后画面上完全一样（内容和偏移都复原了），所以能无限往下滑。
        // 驱动用 CompositionTarget.Rendering —— 和贴边隐藏那套一个道理：
        // DispatcherTimer 会被消息循环合并，看起来一格一格的。

        Stopwatch _drumClock;
        EventHandler _drumRender;
        double _drumAnimTo;

        void Step(int delta)
        {
            if (delta == 0) return;
            // **值要按整个 delta 走**（拖动时一次可能跨好几格，不能漏）。
            // 动画只滑一格 —— 快速拖动时视觉上略微滞后，但数值永远准确，
            // 比"动画走满、值却只动一格"要安全得多。
            if (_drumIsDay)
                _ddlDays = Math.Max(0, Math.Min(999, _ddlDays + delta));
            else
                _ddlHours = Math.Max(0, Math.Min(23, _ddlHours + delta));

            ApplyDdl();
            RefreshDdlNums();
            AnimateDrum(Math.Sign(delta));
        }

        /// <summary>
        /// 值 +1 时中间那格要落到 v+1 上 —— 而 v+1 此刻在中心**上面**一格，
        /// 所以条带要往下走（Y 变正）。
        /// </summary>
        void AnimateDrum(int delta)
        {
            StopDrumAnim();
            _drumAnimTo = ROW_H * delta;
            _drumClock = Stopwatch.StartNew();

            _drumRender = (s, e) =>
            {
                double ms = _drumClock.Elapsed.TotalMilliseconds;
                double t = Math.Min(1.0, ms / 130.0);
                double k = 1 - Math.Pow(1 - t, 3);          // ease-out
                SetStripY(_drumAnimTo * k);
                if (t < 1.0) return;
                StopDrumAnim();
                RebuildDrum();                              // 滑完重居中，画面上看不出接缝
            };
            CompositionTarget.Rendering += _drumRender;
        }

        void StopDrumAnim()
        {
            if (_drumRender == null) return;
            CompositionTarget.Rendering -= _drumRender;
            _drumRender = null;
        }

        void OnDayHit(object sender, MouseButtonEventArgs e)
        {
            if (e != null) e.Handled = true;
            OpenDrum(DayHit, true);
        }

        void OnHourHit(object sender, MouseButtonEventArgs e)
        {
            if (e != null) e.Handled = true;
            OpenDrum(HourHit, false);
        }

        void OnDrumWheel(object sender, MouseWheelEventArgs e)
        {
            e.Handled = true;
            Step(e.Delta > 0 ? 1 : -1);
        }

        void OnDrumDown(object sender, MouseButtonEventArgs e)
        {
            _drumDragging = true;
            _drumLastY = e.GetPosition(this).Y;
            DrumBox.CaptureMouse();
        }

        /// <summary>往上拖 = 数值变大（和转轮往上滚一致）。每 ~20px 走一格。</summary>
        void OnDrumMove(object sender, MouseEventArgs e)
        {
            if (!_drumDragging) return;
            double y = e.GetPosition(this).Y;
            int steps = (int)Math.Truncate((_drumLastY - y) / 20.0);
            if (steps == 0) return;
            _drumLastY -= steps * 20.0;
            Step(steps);
        }

        void OnDrumUp(object sender, MouseEventArgs e)
        {
            if (!_drumDragging) return;
            _drumDragging = false;
            try { DrumBox.ReleaseMouseCapture(); } catch (Exception) { }
        }

        // 验收用的钩子
        public void SimOpenDay() { OpenDrum(DayHit, true); }
        public void SimOpenHour() { OpenDrum(HourHit, false); }
        public void SimStep(int d) { Step(d); }
        public string DdlState()
        {
            return string.Format("days={0} hours={1} 显示=\"{2}天 {3}小时\" 转轮开着={4}",
                _ddlDays, _ddlHours, DayNum.Text, HourNum.Text, DrumPop.IsOpen);
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
            RefreshDdlNums();
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
