using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;

namespace RemeberEr
{
    /// <summary>
    /// 框选层：铺一张整屏截图，把选区之外压暗，拖出一个框。
    ///
    /// 从 WPF 版移植，交互行为逐条对齐：
    ///   1. 松手之后选区**不消失**，可以拖角微调、拖内部平移；
    ///   2. 确认不靠 Enter —— 松手那个角旁边浮一个 ✓，点它才定；
    ///      设置里开了「快速模式」连 ✓ 都不用点，一框即定。
    ///
    /// 与原版的一处结构性差异（是有意改进，参见 docs/macOS-移植方案.md）：
    /// 原版每块屏铺一层（各铺自己那块屏的截图），因为混 DPI 下一条窗口跨屏
    /// 没法同时正确渲染；macOS 这边是「一条窗口盖住虚拟屏」，
    /// 所以**天然支持跨屏框选**，代价是副屏内容会被缩放。
    /// 本机是单屏，两种做法等价；多屏时的取舍记在移植方案文档里。
    ///
    /// 坐标纪律（原版为此踩过坑，这里必须守住）：
    ///   鼠标位置一律用平台层的物理像素坐标，再按实际渲染比例折算成布局坐标；
    ///   **不信** UI 框架自己的鼠标坐标换算。选区内部用布局坐标绘制，
    ///   对外（裁剪）一律折回物理像素。
    /// </summary>
    public partial class OverlayWindow : Window
    {
        enum Mode { None, Create, Move, Resize }

        const double HANDLE = 7;    // 边缘感知半径（布局单位）
        const double BOX = 38;      // ✓ 容器尺寸（含投影留白）
        const double PAD = 5;       // 容器里板子四周的留白
        const double GAP = 6;       // ✓ 与选区之间的间距

        readonly bool _quick;
        readonly DisplayInfo _display;
        readonly double _mw, _mh;   // 这块屏的物理尺寸

        Canvas _root, _selBox;
        Image _shot;
        Path _veil;
        Rectangle _selRect, _h1, _h2, _h3, _h4;
        Border _sizeTag, _hint;
        TextBlock _sizeText;
        Button _confirmBtn;

        Mode _mode = Mode.None;
        int _edge;                  // 位掩码 1=左 2=上 4=右 8=下
        Point _dragFrom;
        Rect _selAtDragStart;
        Rect _sel;
        Point _last;
        bool _settled;

        /// <summary>选区定下来了：物理像素，相对本屏左上角。</summary>
        public event Action<int, int, int, int> Picked;
        public event Action Cancelled;

        public OverlayWindow(Screenshot shotImg, DisplayInfo display, bool quick)
        {
            Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);

            _quick = quick;
            _display = display;
            _mw = Math.Max(1, display.W);
            _mh = Math.Max(1, display.H);

            _root = this.FindControl<Canvas>("Root");
            _selBox = this.FindControl<Canvas>("SelBox");
            _shot = this.FindControl<Image>("Shot");
            _veil = this.FindControl<Path>("Veil");
            _selRect = this.FindControl<Rectangle>("SelRect");
            _h1 = this.FindControl<Rectangle>("H1");
            _h2 = this.FindControl<Rectangle>("H2");
            _h3 = this.FindControl<Rectangle>("H3");
            _h4 = this.FindControl<Rectangle>("H4");
            _sizeTag = this.FindControl<Border>("SizeTag");
            _sizeText = this.FindControl<TextBlock>("SizeText");
            _confirmBtn = this.FindControl<Button>("ConfirmBtn");
            _hint = this.FindControl<Border>("Hint");

            // 窗口几何：按物理像素摆，尺寸折成本窗口的布局单位。
            // 关键是在窗口「出生前」就把位置和大小定好 ——
            // 原版是先建在主屏再挪到目标屏，结果 WPF 的布局坐标和输入坐标差了缩放倍数。
            double s = display.Scale > 0 ? display.Scale : 1.0;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint((int)Math.Round(display.X), (int)Math.Round(display.Y));
            Width = _mw / s;
            Height = _mh / s;

            var bmp = BitmapUtil.ToBitmap(shotImg);
            _shot.Source = bmp;
            _shot.Width = Width;
            _shot.Height = Height;

            _confirmBtn.Click += (_, __) => Confirm();

            _root.PointerPressed += OnPointerPressed;
            _root.PointerMoved += OnPointerMoved;
            _root.PointerReleased += OnPointerReleased;
            _root.PointerWheelChanged += (_, e) => e.Handled = true;   // 别让滚轮穿透
            KeyDown += OnKeyDown;
            Opened += (_, __) =>
            {
                PlaceHint();
                Paint(default(Rect));
                Program.Log(string.Format(
                    "  框选层已显示：屏={0} 物理={1}x{2} 布局={3}x{4} 缩放={5}",
                    display.Id, _mw, _mh, Width, Height, s));
            };
        }

        /// <summary>整块撤走：先 Hide 再 Close（原版注释里说分层窗口直接 Close 会「分段消失」）。</summary>
        public void Dismiss()
        {
            try { Hide(); } catch (Exception) { }
            try { Close(); } catch (Exception) { }
        }

        // ============================================================ 自检钩子

        /// <summary>
        /// 验收用：按「本屏物理像素」设定一个选区，并把窗口自身渲染成 PNG 存下来。
        ///
        /// 为什么要自己渲染自己：GUI 程序没法只靠日志验证「画得对不对」。
        /// 用 RenderTargetBitmap 抓视觉树，**不依赖系统抓屏权限**，
        /// 因此这条自检路径在授权之前就能跑通 —— 可以把「渲染/坐标不对」
        /// 和「没权限」两类问题分开排查，否则它们都表现为「截图是黑的/空的」。
        /// </summary>
        public void SetSelectionForTest(int ix, int iy, int iw, int ih, string verifyPngPath)
        {
            _sel = FromImagePixels(ix, iy, iw, ih);
            _last = new Point(_sel.Right, _sel.Bottom);
            _settled = true;
            Paint(_sel);

            int gx, gy, gw, gh;
            ToImagePixels(_sel, out gx, out gy, out gw, out gh);
            Program.Log(string.Format(
                "  [自检] 布局选区 = ({0:0.#},{1:0.#}) {2:0.#}x{3:0.#}  →  物理像素 = ({4},{5}) {6}x{7}",
                _sel.X, _sel.Y, _sel.Width, _sel.Height, gx, gy, gw, gh));
            Program.Log(string.Format(
                "  [自检] 换算比例 KX={0:0.####} KY={1:0.####}（窗口 {2:0.#}x{3:0.#} / 屏幕 {4}x{5}）",
                KX, KY, Width, Height, _mw, _mh));
            Program.Log(string.Format("  [自检] ✓ 可见={0} 位置=({1:0.#},{2:0.#})",
                _confirmBtn.IsVisible,
                Canvas.GetLeft(_confirmBtn), Canvas.GetTop(_confirmBtn)));

            if (!string.IsNullOrEmpty(verifyPngPath)) SaveSelfPng(verifyPngPath);
        }

        /// <summary>把本窗口渲染成 PNG（不依赖系统抓屏权限）。</summary>
        void SaveSelfPng(string path)
        {
            try
            {
                int pw = Math.Max(1, (int)Math.Round(Bounds.Width));
                int ph = Math.Max(1, (int)Math.Round(Bounds.Height));
                var rtb = new Avalonia.Media.Imaging.RenderTargetBitmap(
                    new PixelSize(pw, ph), new Vector(96, 96));
                rtb.Render(this);

                // Avalonia 12 起 Save(Stream) 已过时，要显式给出编码器选项。
                // 自检图一律存 PNG —— 无损，便于逐像素比对渲染结果。
                using (var fs = System.IO.File.Create(path))
                    rtb.Save(fs, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());

                Program.Log("  [自检] 窗口截图已写出 -> " + path);
            }
            catch (Exception ex)
            {
                Program.Log("  [自检] 窗口截图失败：" + ex.GetType().Name + " " + ex.Message);
            }
        }

        // ============================================================ 坐标换算

        /// <summary>布局单位 / 物理像素。渲染时用这个比例把物理选区折回布局坐标。</summary>
        double KX { get { return Width > 0 ? Width / _mw : 1.0; } }
        double KY { get { return Height > 0 ? Height / _mh : 1.0; } }

        /// <summary>鼠标位置（布局坐标）。取平台层物理像素再折算，不信 UI 框架的换算。</summary>
        Point CursorInLayout()
        {
            var p = Program.Platform.GetCursorPosition();
            return new Point((p.X - _display.X) * KX, (p.Y - _display.Y) * KY);
        }

        /// <summary>布局选区 → 本屏物理像素。</summary>
        void ToImagePixels(Rect sel, out int x, out int y, out int w, out int h)
        {
            x = (int)Math.Round(sel.X / KX);
            y = (int)Math.Round(sel.Y / KY);
            w = (int)Math.Round(sel.Width / KX);
            h = (int)Math.Round(sel.Height / KY);
        }

        /// <summary>本屏物理像素 → 布局选区（自动化验收画框用）。</summary>
        Rect FromImagePixels(double x, double y, double w, double h)
        {
            return new Rect(x * KX, y * KY, w * KX, h * KY);
        }

        // ============================================================ 绘制

        /// <summary>四周压暗：整屏矩形 + 选区矩形，EvenOdd 把选区挖成洞。</summary>
        void UpdateVeil(Rect sel)
        {
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.SetFillRule(FillRule.EvenOdd);
                ctx.BeginFigure(new Point(0, 0), true);
                ctx.LineTo(new Point(Width, 0));
                ctx.LineTo(new Point(Width, Height));
                ctx.LineTo(new Point(0, Height));
                ctx.EndFigure(true);

                if (sel.Width > 0 && sel.Height > 0)
                {
                    ctx.BeginFigure(new Point(sel.X, sel.Y), true);
                    ctx.LineTo(new Point(sel.Right, sel.Y));
                    ctx.LineTo(new Point(sel.Right, sel.Bottom));
                    ctx.LineTo(new Point(sel.X, sel.Bottom));
                    ctx.EndFigure(true);
                }
            }
            _veil.Data = g;
        }

        void Paint(Rect sel)
        {
            bool has = sel.Width > 0 && sel.Height > 0;

            _veil.Fill = new SolidColorBrush(has
                ? Color.FromArgb(0x8C, 0, 0, 0)
                : Color.FromArgb(0x66, 0, 0, 0));
            UpdateVeil(sel);

            _selBox.IsVisible = has;
            _sizeTag.IsVisible = has;
            // 快速模式不显示 ✓ —— 松手就定了，摆个按钮反而让人以为还要点
            _confirmBtn.IsVisible = has && _settled && !_quick;
            _hint.IsVisible = !has;
            if (!has) return;

            Canvas.SetLeft(_selBox, sel.X);
            Canvas.SetTop(_selBox, sel.Y);
            _selRect.Width = sel.Width;
            _selRect.Height = sel.Height;

            // 四个角各放一个小方块（原版是画在矩形边框上的控制点）。
            // 位置以矩形为参照，所以要减掉方块自身的一半。
            PlaceHandle(_h1, 0, 0);
            PlaceHandle(_h2, sel.Width, 0);
            PlaceHandle(_h3, 0, sel.Height);
            PlaceHandle(_h4, sel.Width, sel.Height);

            int ix, iy, iw, ih;
            ToImagePixels(sel, out ix, out iy, out iw, out ih);
            _sizeText.Text = iw + " × " + ih;
            bool above = sel.Y > 26;
            Canvas.SetLeft(_sizeTag, Math.Max(6, Math.Min(sel.X, Width - 96)));
            Canvas.SetTop(_sizeTag, above ? sel.Y - 24 : sel.Y + 8);

            PlaceConfirm(sel);
        }

        static void PlaceHandle(Rectangle h, double x, double y)
        {
            Canvas.SetLeft(h, x - h.Width / 2);
            Canvas.SetTop(h, y - h.Height / 2);
        }

        void PlaceHint()
        {
            _hint.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var sz = _hint.DesiredSize;
            Canvas.SetLeft(_hint, Math.Max(0, (Width - sz.Width) / 2));
            Canvas.SetTop(_hint, Math.Max(0, Height - sz.Height - 60));
        }

        /// <summary>
        /// ✓ 贴在「鼠标松手那个角」的**外侧**，不压住选区内容。
        /// 外侧放不下就沿同一条边平移，再不行才翻到对侧；极端情况才夹回可视范围。
        /// </summary>
        void PlaceConfirm(Rect sel)
        {
            if (!_confirmBtn.IsVisible) return;

            bool right = _last.X >= sel.Left + sel.Width / 2;
            bool bottom = _last.Y >= sel.Top + sel.Height / 2;

            double bx = right ? sel.Right + GAP - PAD : sel.Left - BOX - GAP + PAD;
            double by = bottom ? sel.Bottom + GAP - PAD : sel.Top - BOX - GAP + PAD;

            double maxX = Math.Max(0, Width - BOX);
            double maxY = Math.Max(0, Height - BOX);
            double x = Math.Max(0, Math.Min(bx, maxX));
            double y = Math.Max(0, Math.Min(by, maxY));

            // 候选顺序：先斜向外侧 → 沿上下让 → 沿左右让 → 翻到本侧另一头
            var cands = new[]
            {
                new Point(bx, by),
                new Point(bx, bottom ? by : sel.Top + GAP - PAD),
                new Point(right ? bx : sel.Left + GAP - PAD, by),
                new Point(right ? sel.Right - BOX - GAP + PAD : bx, by),
                new Point(bx, bottom ? sel.Bottom - BOX - GAP + PAD : by),
            };

            foreach (var c in cands)
            {
                if (c.X >= 0 && c.Y >= 0 && c.X <= maxX && c.Y <= maxY) { x = c.X; y = c.Y; break; }
            }

            Canvas.SetLeft(_confirmBtn, x);
            Canvas.SetTop(_confirmBtn, y);
        }

        static Rect ToRect(Point a, Point b)
        {
            double x = Math.Min(a.X, b.X), y = Math.Min(a.Y, b.Y);
            return new Rect(x, y, Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));
        }

        Rect Clamp(Rect r)
        {
            double w = Math.Min(r.Width, Width);
            double h = Math.Min(r.Height, Height);
            double x = Math.Max(0, Math.Min(r.X, Width - w));
            double y = Math.Max(0, Math.Min(r.Y, Height - h));
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

        Rect ConfirmRect()
        {
            return new Rect(Canvas.GetLeft(_confirmBtn), Canvas.GetTop(_confirmBtn), BOX, BOX);
        }

        // ============================================================ 交互

        void OnPointerPressed(object sender, PointerPressedEventArgs e)
        {
            var props = e.GetCurrentPoint(_root).Properties;
            if (!props.IsLeftButtonPressed) return;

            var p = CursorInLayout();
            _last = p;

            // ✓ 的命中判定自己做，不走框架的 HitTest ——
            // 混 DPI 下框架的输入坐标不一定和我们绘制用的布局坐标一致，
            // 靠它会出现「看得见点不着」（原版注释里的原话）。
            if (_confirmBtn.IsVisible && ConfirmRect().Contains(p))
            {
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
            e.Handled = true;
        }

        void OnPointerMoved(object sender, PointerEventArgs e)
        {
            var p = CursorInLayout();
            _last = p;

            if (_mode == Mode.None)
            {
                bool onBtn = _confirmBtn.IsVisible && ConfirmRect().Contains(p);
                _confirmBtn.Opacity = onBtn ? 0.85 : 1.0;
                if (onBtn) { Cursor = new Cursor(StandardCursorType.Hand); return; }
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
                    r = new Rect(r.X + (p.X - _dragFrom.X), r.Y + (p.Y - _dragFrom.Y), r.Width, r.Height);
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

        void OnPointerReleased(object sender, PointerReleasedEventArgs e)
        {
            if (_mode == Mode.None) return;
            _mode = Mode.None;

            int ix, iy, iw, ih;
            ToImagePixels(_sel, out ix, out iy, out iw, out ih);

            // 太小的框选（基本等于误点）→ 清掉，回到「还没选」的状态
            if (iw < 6 || ih < 6)
            {
                _sel = default(Rect);
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
                Cursor = new Cursor(_sel.Width > 0 && _sel.Contains(p)
                    ? StandardCursorType.SizeAll : StandardCursorType.Cross);
                return;
            }
            switch (e)
            {
                case 1: case 4: Cursor = new Cursor(StandardCursorType.SizeWestEast); break;
                case 2: case 8: Cursor = new Cursor(StandardCursorType.SizeNorthSouth); break;
                case 3: case 12: Cursor = new Cursor(StandardCursorType.TopLeftCorner); break;
                default: Cursor = new Cursor(StandardCursorType.TopRightCorner); break;
            }
        }

        void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) { e.Handled = true; Cancel(); return; }
            if (e.Key == Key.Enter) { e.Handled = true; Confirm(); }
        }

        void Confirm()
        {
            if (!_settled || _sel.Width <= 0) return;

            int ix, iy, iw, ih;
            ToImagePixels(_sel, out ix, out iy, out iw, out ih);
            if (iw < 6 || ih < 6) return;

            var h = Picked;
            if (h != null) h(ix, iy, iw, ih);
        }

        void Cancel()
        {
            var c = Cancelled;
            if (c != null) c();
        }
    }
}
