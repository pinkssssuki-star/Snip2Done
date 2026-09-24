using System;
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
    /// 框选层：盖住鼠标所在的那块屏，铺上整屏截图，拖出一个框。
    ///
    /// 和 Electron 版不同的两点：
    ///   1. 松手之后选区还在，可以拖角微调、拖内部平移；
    ///   2. 确认不靠 Enter —— 松手那个角旁边会浮一个 ✓，点它就定；
    ///      设置里开了「快速模式」连 ✓ 都不用点，一框即定。
    ///
    /// ⚠️ 坐标一律走「物理像素」，鼠标位置用 GetCursorPos 拿，不用 e.GetPosition。
    ///
    /// 为什么：窗口是在主屏（100%）上创建的，随后被 SetWindowPos 挪到 150% 那块屏，
    /// WPF 没跟上这次 DPI 变化 —— 实测 wpfScale 仍是 1.0，于是**两套坐标系打架**：
    ///   · 布局/渲染按 1 DIP = 1 物理像素（窗口 2560 DIP 对应 2560 物理像素）
    ///   · 鼠标输入却按 1.5 折算（拖 900 物理像素只报 600 DIP）
    /// 结果选区框画在 600 处、鼠标在 900 处 —— 看着只圈住三分之二，实际却按 1.5 倍裁出去，
    /// 用户看到的就是「截图过大、溢出屏幕」。
    ///
    /// 现在改成：鼠标用 GetCursorPos（本进程 per-monitor aware，拿到的是真物理像素），
    /// 选区以物理像素为准，只在绘制时按「布局 DIP ÷ 物理像素」的实际比例折回去。
    /// 这个比例不管 WPF 认为 DPI 是多少都成立。
    /// </summary>
    public partial class OverlayWindow : Window
    {
        enum Mode { None, Create, Move, Resize }

        const double HANDLE = 7;    // 边缘感知半径（布局 DIP）
        const double BOX = 38;
        const double BAR = BOX * 2;   // 图钉 + 勾，动作条总宽      // ✓ 容器尺寸（含投影留白）
        const double PAD = 5;       // 容器里板子四周的留白
        const double GAP = 6;       // ✓ 与选区之间的间距

        readonly bool _quick;            // 快速模式：松手即定
        readonly Interop.RECT _monitor;
        readonly int _mw, _mh;           // 这块屏的物理尺寸

        Mode _mode = Mode.None;
        int _edge;                       // 位掩码 1=左 2=上 4=右 8=下
        Point _dragFrom;                 // 布局坐标
        Rect _selAtDragStart;
        Rect _sel;                       // 当前选区（布局坐标，绘制与命中判定都用它）
        Point _last;                     // 最后一次鼠标位置（布局坐标）
        bool _settled;

        /// <summary>这块框选层盖的是哪块屏（物理矩形）。</summary>
        public Interop.RECT Monitor { get { return _monitor; } }

        /// <summary>整块撤走：先 Hide 再 Close。多屏时由主窗口统一收全部。</summary>
        public void Dismiss()
        {
            try { Hide(); } catch (Exception) { }
            try { Close(); } catch (Exception) { }
        }

        /// <summary>选区（截图像素，相对本屏左上角）。</summary>
        public event Action<int, int, int, int> Picked;
        public event Action Cancelled;
        /// <summary>点了图钉：把这块图钉在屏幕上（和 Snipaste 的贴图一样，不落库）。</summary>
        public event Action<int, int, int, int> Pinned;

        public OverlayWindow(BitmapSource shot, Interop.RECT monitor, double dpiScale, bool quick)
        {
            InitializeComponent();
            _quick = quick;
            _monitor = monitor;
            _mw = Math.Max(1, monitor.Right - monitor.Left);
            _mh = Math.Max(1, monitor.Bottom - monitor.Top);

            // 关键：在窗口「出生之前」就按目标屏的 DPI 把逻辑坐标算好。
            // 之前是把窗口建在主屏再 SetWindowPos 挪过来，WPF 不跟这次 DPI 变化，
            // 于是它的布局坐标和输入坐标差了 1.5 倍（渲染按 1:1、鼠标按 1.5 折算），
            // 结果就是你拖 900 像素它只认 600，选区和实际裁剪对不上。
            double s = dpiScale > 0 ? dpiScale : 1.0;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = monitor.Left / s;
            Top = monitor.Top / s;
            Width = _mw / s;
            Height = _mh / s;

            Shot.Source = shot;

            Loaded += (s2, e) =>
            {
                if (Program.DpiDiag) LogDiag();
                Paint(Rect.Empty);
            };
        }

        void LogDiag()
        {
            Interop.RECT wr;
            Interop.GetWindowRect(new WindowInteropHelper(this).Handle, out wr);
            var dpi = VisualTreeHelper.GetDpi(this);
            int aware;
            try { Interop.GetProcessDpiAwareness(IntPtr.Zero, out aware); } catch (Exception) { aware = -1; }
            Program.Log(string.Format(
                "  DPI 诊断: 窗口物理={0}x{1} 布局DIP={2}x{3} wpfScale={4:0.###} 感知={5} 屏={6}x{7} 布局比={8:0.###}",
                wr.Right - wr.Left, wr.Bottom - wr.Top, (int)ActualWidth, (int)ActualHeight,
                dpi.DpiScaleX, aware, _mw, _mh, KR));
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var h = new WindowInteropHelper(this).Handle;
            Interop.HideFromAltTab(h);

            // 直接按物理像素摆窗，绕开 WPF 那套 DIP 换算
            Interop.SetWindowPos(h, Interop.HWND_TOPMOST,
                _monitor.Left, _monitor.Top, _mw, _mh,
                Interop.SWP_NOACTIVATE | Interop.SWP_SHOWWINDOW);

            // 验收用的自动流程只跑光标那块屏，别每块屏都来一遍
            if (Program.AutoRegionW > 0 && IsCursorMonitor()) ScheduleAutoPick();
            else if (Program.SynthDrag && IsCursorMonitor()) ScheduleSynthDrag();
        }

        /// <summary>验收用：合成拖拽只该在光标那块屏上跑，别每块屏都来一遍。</summary>
        bool IsCursorMonitor()
        {
            Interop.RECT cur;
            double s;
            if (!Interop.MonitorUnderCursor(out cur, out s)) return true;
            return Interop.SameRect(cur, _monitor);
        }

        // ============================================================ 坐标换算

        /// <summary>布局 DIP / 物理像素。渲染时用这个比例把物理选区折回布局坐标。</summary>
        double KR { get { return ActualWidth > 0 ? ActualWidth / _mw : 1.0; } }
        double KRY { get { return ActualHeight > 0 ? ActualHeight / _mh : 1.0; } }

        /// <summary>鼠标位置（布局坐标）。用 GetCursorPos 拿物理像素再折算，不信 WPF 的鼠标换算。</summary>
        Point CursorInLayout()
        {
            Interop.POINT p;
            Interop.GetCursorPos(out p);
            return new Point((p.X - _monitor.Left) * KR, (p.Y - _monitor.Top) * KRY);
        }

        /// <summary>布局选区 → 截图像素。</summary>
        void ToImagePixels(Rect sel, out int x, out int y, out int w, out int h)
        {
            double kx = KR, ky = KRY;
            x = (int)Math.Round(sel.X / kx);
            y = (int)Math.Round(sel.Y / ky);
            w = (int)Math.Round(sel.Width / kx);
            h = (int)Math.Round(sel.Height / ky);
        }

        /// <summary>截图像素 → 布局选区（自动化验收画框用）。</summary>
        Rect FromImagePixels(int x, int y, int w, int h)
        {
            return new Rect(x * KR, y * KRY, w * KR, h * KRY);
        }

        // ============================================================ 自动化验收

        void ScheduleAutoPick()
        {
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
            t.Tick += (s, e) =>
            {
                t.Stop();
                int w = Program.AutoRegionW, h = Program.AutoRegionH;
                _sel = FromImagePixels(Math.Max(0, (_mw - w) / 2), Math.Max(0, (_mh - h) / 2), w, h);
                _last = new Point(_sel.Right, _sel.Bottom);
                _settled = true;
                Paint(_sel);

                if (Program.OverlayShot != null)
                {
                    var t2 = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(260) };
                    t2.Tick += (a, b) =>
                    {
                        t2.Stop();
                        Interop.RECT r;
                        Interop.GetWindowRect(new WindowInteropHelper(this).Handle, out r);
                        Capture.SaveRegion(Program.OverlayShot, r, 0);
                        Program.Log("  框选层已截图 -> " + Program.OverlayShot);
                        if (Program.AutoPin) Pin(); else Confirm();
                    };
                    t2.Start();
                }
                // --autopin 必须两条支路都管到：只写在有 --overlayshot 的那条里，
                // 不传 overlayshot 时就会静默走成「确认」，测出来的是勾不是图钉。
                else if (Program.AutoPin) Pin();
                else Confirm();
            };
            t.Start();
        }

        /// <summary>
        /// 合成一次真实鼠标拖拽（SetCursorPos + mouse_event）再点一下 ✓。
        /// 这条路走的是真实输入通道，能复现混 DPI 下的坐标问题 —— 自动选区那条是纯内部往返，测不出来。
        /// </summary>
        void ScheduleSynthDrag()
        {
            int x1 = _monitor.Left + (int)(_mw * 0.25);
            int y1 = _monitor.Top + (int)(_mh * 0.22);
            int x2 = x1 + 900, y2 = y1 + 560;

            var steps = new System.Collections.Generic.List<System.Tuple<int, Action>>();
            steps.Add(System.Tuple.Create<int, Action>(350, () =>
            {
                Program.Log(string.Format("  合成拖拽 ({0},{1}) → ({2},{3})，期望裁剪 900x560", x1, y1, x2, y2));
                Interop.SetCursorPos(x1, y1);
                if (Program.OverlayShot != null)
                {
                    // 还没框选时的样子 —— 用来检查提示条
                    Interop.RECT r;
                    Interop.GetWindowRect(new WindowInteropHelper(this).Handle, out r);
                    Capture.SaveRegion(Program.OverlayShot + ".hint.png", r, 0);
                    Program.Log("  提示条截图 -> " + Program.OverlayShot + ".hint.png");
                }
            }));
            steps.Add(System.Tuple.Create<int, Action>(80, Interop.MouseDown));
            for (int k = 1; k <= 6; k++)
            {
                int px = x1 + (x2 - x1) * k / 6, py = y1 + (y2 - y1) * k / 6;
                steps.Add(System.Tuple.Create<int, Action>(45, () => Interop.SetCursorPos(px, py)));
            }
            steps.Add(System.Tuple.Create<int, Action>(80, Interop.MouseUp));
            steps.Add(System.Tuple.Create<int, Action>(250, () =>
            {
                int ix, iy, iw, ih;
                ToImagePixels(_sel, out ix, out iy, out iw, out ih);
                Program.Log(string.Format("  拖完：布局={0}x{1} 换算比例={2:0.###} 选区像素=({3},{4}) {5}x{6}",
                    (int)ActualWidth, (int)ActualHeight, KR, ix, iy, iw, ih));
                Program.Log(string.Format("  ✓ 状态: Visibility={0} Canvas=({1},{2}) 尺寸={3}x{4} settled={5}",
                    ActionBar.Visibility, Canvas.GetLeft(ActionBar), Canvas.GetTop(ActionBar),
                    ActionBar.ActualWidth, ActionBar.ActualHeight, _settled));

                if (Program.OverlayShot != null)
                {
                    Interop.RECT r;
                    Interop.GetWindowRect(new WindowInteropHelper(this).Handle, out r);
                    Capture.SaveRegion(Program.OverlayShot, r, 0);
                    Program.Log("  框选层已截图 -> " + Program.OverlayShot);
                }

                // ✓ 现在在选区外侧，位置由 PlaceConfirm 决定，这里直接取它的中心
                var br = ConfirmRect();
                int bx = _monitor.Left + (int)Math.Round((br.X + br.Width / 2) / KR);
                int by = _monitor.Top + (int)Math.Round((br.Y + br.Height / 2) / KRY);
                Program.Log(string.Format("  合成点击 ✓ 于 ({0},{1})", bx, by));
                Interop.SetCursorPos(bx, by);
            }));
            steps.Add(System.Tuple.Create<int, Action>(90, Interop.MouseDown));
            steps.Add(System.Tuple.Create<int, Action>(90, Interop.MouseUp));
            steps.Add(System.Tuple.Create<int, Action>(500, () =>
                Program.Log("  点击后：settled=" + _settled + " 是否已选中=" + (_sel.Width > 0))));

            RunSteps(steps, 0);
        }

        void RunSteps(System.Collections.Generic.List<System.Tuple<int, Action>> steps, int i)
        {
            if (i >= steps.Count) return;
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(steps[i].Item1) };
            t.Tick += (s, e) => { t.Stop(); steps[i].Item2(); RunSteps(steps, i + 1); };
            t.Start();
        }

        // ============================================================ 绘制

        /// <summary>四周压暗：整屏矩形 + 选区矩形，EvenOdd 把选区挖成洞。</summary>
        void UpdateVeil(Rect sel)
        {
            var g = new GeometryGroup { FillRule = FillRule.EvenOdd };
            g.Children.Add(new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight)));
            if (sel.Width > 0 && sel.Height > 0) g.Children.Add(new RectangleGeometry(sel));
            Veil.Data = g;
        }

        void Paint(Rect sel)
        {
            bool has = sel.Width > 0 && sel.Height > 0;

            Veil.Fill = new SolidColorBrush(has
                ? Color.FromArgb(0x8C, 0, 0, 0)
                : Color.FromArgb(0x66, 0, 0, 0));
            UpdateVeil(sel);

            Sel.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
            SizeTag.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
            // 快速模式不显示 ✓ —— 松手就定了，摆个按钮反而让人以为还要点
            ActionBar.Visibility = (has && _settled && !_quick) ? Visibility.Visible : Visibility.Collapsed;
            Hint.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
            if (!has) return;

            Canvas.SetLeft(Sel, sel.X);
            Canvas.SetTop(Sel, sel.Y);
            Sel.Width = sel.Width;
            Sel.Height = sel.Height;

            int ix, iy, iw, ih;
            ToImagePixels(sel, out ix, out iy, out iw, out ih);
            SizeText.Text = iw + " × " + ih;
            bool above = sel.Y > 26;
            Canvas.SetLeft(SizeTag, Math.Max(6, Math.Min(sel.X, ActualWidth - 96)));
            Canvas.SetTop(SizeTag, above ? sel.Y - 24 : sel.Y + 8);

            PlaceConfirm(sel);
        }

        /// <summary>
        /// 动作条（📌 + ✓）贴在「鼠标松手那个角」的**外侧**，不压住选区内容。
        /// 外侧放不下就沿同一条边平移，再不行才翻到对侧；极端情况才夹回可视范围。
        /// </summary>
        void PlaceConfirm(Rect sel)
        {
            if (ActionBar.Visibility != Visibility.Visible) return;

            bool right = _last.X >= sel.Left + sel.Width / 2;
            bool bottom = _last.Y >= sel.Top + sel.Height / 2;

            // 容器 BOX 里板子 34、四周各留 PAD 给投影；按板子贴边，所以整体外扩回去
            double bx = right ? sel.Right + GAP - PAD : sel.Left - BAR - GAP + PAD;
            double by = bottom ? sel.Bottom + GAP - PAD : sel.Top - BOX - GAP + PAD;

            // 候选顺序：先斜向外侧 → 沿上下让 → 沿左右让 → 翻到本侧另一头
            var cands = new[]
            {
                new Point(bx, by),
                new Point(bx, bottom ? by : sel.Top + GAP - PAD),
                new Point(right ? bx : sel.Left + GAP - PAD, by),
                new Point(right ? sel.Right - BAR - GAP + PAD : bx, by),
                new Point(bx, bottom ? sel.Bottom - BOX - GAP + PAD : by),
            };

            double maxX = Math.Max(0, ActualWidth - BAR);
            double maxY = Math.Max(0, ActualHeight - BOX);
            double x = bx, y = by;
            foreach (var c in cands)
            {
                if (c.X >= 0 && c.Y >= 0 && c.X <= maxX && c.Y <= maxY) { x = c.X; y = c.Y; break; }
                // 都不合适就退回夹紧的斜向位置
                x = Math.Max(0, Math.Min(bx, maxX));
                y = Math.Max(0, Math.Min(by, maxY));
            }

            Canvas.SetLeft(ActionBar, x);
            Canvas.SetTop(ActionBar, y);
        }

        static Rect ToRect(Point a, Point b)
        {
            double x = Math.Min(a.X, b.X), y = Math.Min(a.Y, b.Y);
            return new Rect(x, y, Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));
        }

        Rect Clamp(Rect r)
        {
            double w = Math.Min(r.Width, ActualWidth);
            double h = Math.Min(r.Height, ActualHeight);
            double x = Math.Max(0, Math.Min(r.X, ActualWidth - w));
            double y = Math.Max(0, Math.Min(r.Y, ActualHeight - h));
            return new Rect(x, y, w, h);
        }

        int HitEdge(Point p, Rect r)
        {
            if (r.Width <= 0) return 0;
            int e = 0;
            if (Math.Abs(p.X - r.Left) <= HANDLE && p.Y >= r.Top - HANDLE && p.Y <= r.Bottom + HANDLE) e |= 1;
            if (Math.Abs(p.Y - r.Top) <= HANDLE && p.X >= r.Left - HANDLE && p.X <= r.Right + HANDLE) e |= 2;
            if (Math.Abs(p.X - r.Right) <= HANDLE && p.Y >= r.Top - HANDLE && p.Y <= r.Bottom + HANDLE) e |= 4;
            if (Math.Abs(p.Y - r.Bottom) <= HANDLE && p.X >= r.Left - HANDLE && p.X <= r.Right + HANDLE) e |= 8;
            return e;
        }

        // ============================================================ 交互

        void OnMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;

            // 多屏时键盘焦点可能还在别的那块屏上，点一下就把它抢过来，Esc/Enter 才管用
            try { Activate(); } catch (Exception) { }

            var p = CursorInLayout();
            _last = p;

            // ✓ / 📌 的命中判定自己做，不走 WPF 的 HitTest —— 混 DPI 下 WPF 的输入坐标
            // 不一定和我们绘制用的布局坐标一致，靠它会出现「看得见点不着」。
            if (ActionBar.Visibility == Visibility.Visible && ConfirmRect().Contains(p))
            {
                var local = new Point(p.X - Canvas.GetLeft(ActionBar), p.Y - Canvas.GetTop(ActionBar));
                if (local.X < BOX) { Pin(); return; }   // 左边那半是图钉
                Confirm();
                return;
            }

            if (e.ClickCount >= 2 && _settled) { Confirm(); return; }

            int edge = HitEdge(p, _sel);
            if (edge != 0) { _mode = Mode.Resize; _edge = edge; }
            else if (_sel.Width > 0 && _sel.Contains(p)) { _mode = Mode.Move; }
            else { _mode = Mode.Create; _sel = new Rect(p, new Size(0, 0)); _settled = false; }

            _dragFrom = p;
            _selAtDragStart = _sel;
            Paint(_sel);
        }

        /// <summary>✓ 按钮在布局坐标里的矩形（自己算，不依赖 WPF 的 HitTest）。</summary>
        Rect ConfirmRect()
        {
            return new Rect(Canvas.GetLeft(ActionBar), Canvas.GetTop(ActionBar), BAR, BOX);
        }

        void OnMouseMove(object sender, MouseEventArgs e)
        {
            var p = CursorInLayout();
            _last = p;

            if (_mode == Mode.None)
            {
                bool onBtn = ActionBar.Visibility == Visibility.Visible && ConfirmRect().Contains(p);
                ActionBar.Opacity = onBtn ? 0.85 : 1.0;
                if (onBtn) { Cursor = Cursors.Hand; return; }
                UpdateCursor(p);
                return;
            }

            switch (_mode)
            {
                case Mode.Create:
                    _sel = ToRect(_dragFrom, p);
                    break;

                case Mode.Move:
                {
                    var r = _selAtDragStart;
                    r.Offset(p.X - _dragFrom.X, p.Y - _dragFrom.Y);
                    _sel = Clamp(r);
                    break;
                }

                case Mode.Resize:
                {
                    var r = _selAtDragStart;
                    double l = r.Left, t = r.Top, rt = r.Right, b = r.Bottom;
                    if ((_edge & 1) != 0) l = p.X;
                    if ((_edge & 2) != 0) t = p.Y;
                    if ((_edge & 4) != 0) rt = p.X;
                    if ((_edge & 8) != 0) b = p.Y;
                    _sel = Clamp(new Rect(Math.Min(l, rt), Math.Min(t, b),
                        Math.Abs(rt - l), Math.Abs(b - t)));
                    break;
                }
            }
            Paint(_sel);
        }

        void OnMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_mode == Mode.None) return;
            _mode = Mode.None;

            int ix, iy, iw, ih;
            ToImagePixels(_sel, out ix, out iy, out iw, out ih);

            // 太小的框选（基本等于误点）→ 清掉，回到「还没选」的状态
            if (iw < 6 || ih < 6)
            {
                _sel = Rect.Empty;
                _settled = false;
                Paint(_sel);
                return;
            }

            _settled = true;
            Paint(_sel);

            if (_quick) Confirm();   // 快速模式：一框即定
        }

        void UpdateCursor(Point p)
        {
            int e = HitEdge(p, _sel);
            if (e == 0)
            {
                Cursor = (_sel.Width > 0 && _sel.Contains(p)) ? Cursors.SizeAll : Cursors.Cross;
                return;
            }
            switch (e)
            {
                case 1: case 4: Cursor = Cursors.SizeWE; break;
                case 2: case 8: Cursor = Cursors.SizeNS; break;
                case 3: case 12: Cursor = Cursors.SizeNWSE; break;   // 左上 / 右下
                default: Cursor = Cursors.SizeNESW; break;           // 右上 / 左下
            }
        }

        void OnConfirmClick(object sender, RoutedEventArgs e) { Confirm(); }

        void Confirm()
        {
            if (!_settled || _sel.Width <= 0) return;

            int ix, iy, iw, ih;
            ToImagePixels(_sel, out ix, out iy, out iw, out ih);
            if (iw < 6 || ih < 6) return;

            var h = Picked;
            if (h != null) h(ix, iy, iw, ih);
        }

        /// <summary>
        /// 点图钉：把这块图钉在屏幕上。
        /// 不走 Picked（那条路会弹归类窗、落库）——「钉住」和「记下来」是两条路。
        /// </summary>
        void Pin()
        {
            if (!_settled || _sel.Width <= 0) return;

            int ix, iy, iw, ih;
            ToImagePixels(_sel, out ix, out iy, out iw, out ih);
            if (iw < 6 || ih < 6) return;

            var h = Pinned;
            if (h != null) h(ix, iy, iw, ih);
        }

        void Cancel()
        {
            var c = Cancelled;
            if (c != null) c();
        }

        void OnRightUp(object sender, MouseButtonEventArgs e) { e.Handled = true; Cancel(); }

        void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) { e.Handled = true; Cancel(); return; }
            if (e.Key == Key.Enter) { e.Handled = true; Confirm(); return; }
            // P = 钉在屏幕上（和 Snipaste 一个键位习惯）
            if (e.Key == Key.P && _settled) { e.Handled = true; Pin(); }
        }
    }
}
