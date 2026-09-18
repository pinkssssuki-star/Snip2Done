using System;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RemeberEr
{
    /// <summary>
    /// 截图后的归类窗：「这一张，是 Todo 还是 Wantdo？」
    /// 尺寸和交互照着 src/renderer/chooser/ 搬：312×212、Esc 算了、1/2/3 直接选、
    /// 标题留空则回落到截图时的窗口标题、右键取消。
    /// </summary>
    public partial class ChooserWindow : Window
    {
        public class Payload
        {
            public byte[] Png;
            /// <summary>256px 宽的缩略图 PNG，落库时一并写到 images/&lt;id&gt;.thumb.png</summary>
            public byte[] ThumbPng;
            public BitmapSource Preview;
            public string App;
            public string Title;
            /// <summary>
            /// 「手动记一笔」用：没有截图、标题也已经填好了，只借这个面板来选时间。
            /// 这种情况下藏掉缩略图和标题框，只留三个按钮，标题换成「什么时候到期？」。
            /// </summary>
            public bool Manual;
        }

        readonly Payload _p;
        readonly Interop.RECT _monitor;   // 截图那块屏（物理像素）
        readonly int _selX, _selY, _selW, _selH;   // 选区（相对该屏左上角，物理像素）
        readonly double _scale;           // 该屏的 DPI 缩放，用来把 DIP 换成物理像素

        // 窗口的逻辑尺寸（XAML 里 Width/Height 是 312x212）
        const int W_DIP = 312, H_DIP = 212;
        // 手动模式（没有缩略图、没有标题框）内容矮一大截，窗口太高会空出一大片
        const int H_MANUAL_DIP = 132;
        int _hDip = H_DIP;

        // 摆好之后的物理位置与尺寸（OnSourceInitialized 里用 SetWindowPos 落实）
        int _physX, _physY, _physW, _physH;

        /// <summary>choice, typedTitle。choice 为 null 表示取消。</summary>
        public event Action<string, string, int> Decided;

        public ChooserWindow(Payload p, Interop.RECT monitor, double scale,
                             int selX, int selY, int selW, int selH)
        {
            InitializeComponent();
            _p = p;
            _monitor = monitor;
            _scale = scale > 0 ? scale : 1.0;
            _selX = selX; _selY = selY; _selW = selW; _selH = selH;

            if (p.Preview != null) Thumb.Source = p.Preview;
            BuildSourceLine(p.App, p.Title);
            TitleBox.Text = p.Title ?? "";

            if (p.Manual)
            {
                // 手动记一笔：已经知道是什么事了，只差一个时间
                Thumb.Visibility = Visibility.Collapsed;
                SrcText.Visibility = Visibility.Collapsed;
                HeadText.Text = string.IsNullOrEmpty(p.Title) ? "这件事什么时候到期？" : "「" + p.Title + "」什么时候到期？";
                TitleBox.Visibility = Visibility.Collapsed;
                WantLabel.Text = "无期限";
                WantSub.Visibility = Visibility.Collapsed;
                _hDip = H_MANUAL_DIP;
            }

            ComputePhysicalPlacement();

            // 和框选层同一个道理：窗口必须在「出生之前」就按目标屏的 DPI 算好逻辑坐标，
            // 否则 WPF 会按主屏的 DPI 建窗，之后挪过去也不跟，位置和渲染比例都会错。
            // 之前用 SystemParameters.WorkArea 做钳制 —— 那是主屏的工作区，
            // 结果副屏截完图，归类窗被硬拉回主屏左下角。
            Left = _physX / _scale;
            Top = _physY / _scale;
            Width = W_DIP;
            Height = _hDip;

            Loaded += (s, e) =>
            {
                if (!p.Manual) { TitleBox.Focus(); TitleBox.SelectAll(); }
                if (Program.AutoChoose != null) ScheduleAutoPick();
            };
        }

        /// <summary>
        /// 贴着选区的右下角外侧放，越界就往里挪；钳制范围是**截图那块屏**，不是主屏。
        /// </summary>
        void ComputePhysicalPlacement()
        {
            _physW = (int)Math.Round(W_DIP * _scale);
            _physH = (int)Math.Round(_hDip * _scale);

            int monL = _monitor.Left, monT = _monitor.Top;
            int monR = _monitor.Right, monB = _monitor.Bottom;

            // 选区右下角，往下 10 物理像素
            int x = monL + _selX + _selW - _physW;
            int y = monT + _selY + _selH + 10;

            int minX = monL + 8, maxX = monR - _physW - 8;
            int minY = monT + 8, maxY = monB - _physH - 8;

            // 下面放不下就翻到选区上方
            if (y > maxY) y = monT + _selY - _physH - 10;

            _physX = Math.Max(minX, Math.Min(x, Math.Max(minX, maxX)));
            _physY = Math.Max(minY, Math.Min(y, Math.Max(minY, maxY)));

            Program.Log(string.Format("  归类窗摆到物理 ({0},{1}) {2}x{3}（屏 {4},{5}-{6},{7} 缩放 {8:0.##}）",
                _physX, _physY, _physW, _physH, monL, monT, monR, monB, _scale));
        }

        /// <summary>
        /// 自动化验收用：先把这个窗口截下来，再自动选一个 —— 不需要人点。
        /// </summary>
        void ScheduleAutoPick()
        {
            var t = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(900),
            };
            t.Tick += (s, e) =>
            {
                t.Stop();
                if (Program.ChooserShot != null)
                {
                    Interop.RECT r;
                    Interop.GetWindowRect(new WindowInteropHelper(this).Handle, out r);
                    Capture.SaveRegion(Program.ChooserShot, r, 40);
                    Program.Log("  归类窗已截图 -> " + Program.ChooserShot);
                }
                Pick(Program.AutoChoose);
            };
            t.Start();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var h = new WindowInteropHelper(this).Handle;
            Interop.HideFromAltTab(h);

            // 逻辑坐标只是让 WPF 把窗口建到正确那块屏上（好让它认对 DPI）；
            // 真正的落点用物理像素钉死，避免 DIP 往返引入误差。
            Interop.SetWindowPos(h, Interop.HWND_TOPMOST, _physX, _physY, _physW, _physH,
                Interop.SWP_NOACTIVATE | Interop.SWP_SHOWWINDOW);

            // 不透明窗口 + DWM 圆角/投影；顺手把边缘的缩放命中区废掉（不能让这个弹窗被拖动改大小）
            Interop.EnableDwmChrome(h);
            HwndSource.FromHwnd(h).AddHook(NoResizeHook);

            if (Program.DpiDiag)
            {
                Interop.RECT wr;
                Interop.GetWindowRect(h, out wr);
                var dpi = VisualTreeHelper.GetDpi(this);
                Program.Log(string.Format(
                    "  归类窗诊断: 窗口物理={0}x{1} 期望={2}x{3} 布局DIP={4}x{5} wpfScale={6:0.###}",
                    wr.Right - wr.Left, wr.Bottom - wr.Top, _physW, _physH,
                    (int)ActualWidth, (int)ActualHeight, dpi.DpiScaleX));
            }
        }

        /// <summary>
        /// 窗口是 ResizeMode=CanResize（这样才有 WS_THICKFRAME，DWM 才给投影），
        /// 但一个归类小弹窗不该被拖边改大小 —— 把命中全当客户区，缩放边框就没有了。
        /// </summary>
        IntPtr NoResizeHook(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled)
        {
            if (msg == Interop.WM_NCHITTEST) { handled = true; return (IntPtr)Interop.HTCLIENT; }
            return IntPtr.Zero;
        }

        /// <summary>
        /// 收摊。**先 Hide 再 Close** —— 直接 Close 一个分层窗口，它的消失是交给销毁流程
        /// 一点点抹掉的，在 150% 那块副屏上能看到「分段消失」；Hide 是一次
        /// ShowWindow(SW_HIDE)，整块原子撤走。
        /// 顺序上 Hide 必须在最前，Close 放最后：Closed 事件里会刷新面板，
        /// 那时候窗口得已经不在屏幕上了。
        /// </summary>
        void Dismiss(string choice, string typed, int days)
        {
            try { Hide(); } catch (Exception) { }
            var d = Decided;
            if (d != null) d(choice, typed, days);
            Close();
        }

        void BuildSourceLine(string app, string title)
        {
            SrcText.Inlines.Clear();
            if (!string.IsNullOrEmpty(app))
            {
                SrcText.Inlines.Add(new Run(app)
                {
                    Foreground = new SolidColorBrush(Color.FromRgb(0xa1, 0xa1, 0xa6)),
                });
            }
            if (!string.IsNullOrEmpty(title))
            {
                if (SrcText.Inlines.Count > 0) SrcText.Inlines.Add(new Run(" · "));
                SrcText.Inlines.Add(new Run(title));
            }
            if (SrcText.Inlines.Count == 0) SrcText.Text = "（没拿到来源信息）";
        }

        void Pick(string choice)
        {
            Dismiss(choice, (TitleBox.Text ?? "").Trim(), ReadDays());
        }

        /// <summary>「几天后」输入框里的数字。空着或者写了 0 → 0（交给 48~72 小时随机）。</summary>
        int ReadDays()
        {
            int n;
            if (DaysBox == null) return 0;
            string s = (DaysBox.Text ?? "").Trim();
            if (s.Length == 0) return 0;
            if (!int.TryParse(s, out n)) return 0;
            if (n <= 0) return 0;
            if (n > 999) n = 999;      // 别让人写出一个公元后的期限
            return n;
        }

        /// <summary>只让输数字，别的一律吞掉。</summary>
        void OnDaysInput(object sender, TextCompositionEventArgs e)
        {
            foreach (char c in e.Text) if (c < '0' || c > '9') { e.Handled = true; return; }
        }

        /// <summary>在「几天」框里按 Enter = 按「两三天」。</summary>
        void OnDaysKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            Pick("soon");
        }

        /// <summary>自动化验收用：不用手点，直接选一个。</summary>
        public void AutoPick(string choice) { Pick(choice); }
        /// <summary>验收用：直接指定天数。</summary>
        public void AutoPickWithDays(string choice, int days)
        {
            if (DaysBox != null && days > 0) DaysBox.Text = days.ToString();
            Pick(choice);
        }

        void Cancel()
        {
            Dismiss(null, null, 0);
        }

        void OnPickToday(object sender, RoutedEventArgs e) { Pick("today"); }
        void OnPickSoon(object sender, RoutedEventArgs e) { Pick("soon"); }
        void OnPickWant(object sender, RoutedEventArgs e) { Pick("want"); }
        void OnRightUp(object sender, MouseButtonEventArgs e) { e.Handled = true; Cancel(); }

        void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) { e.Handled = true; Cancel(); return; }

            // 只有焦点在标题框里打字时才让数字键进输入框，否则 1/2/3 直接选
            bool typing = Keyboard.FocusedElement == TitleBox;

            if (e.Key == Key.Enter)
            {
                // 输入框里回车只是收焦点，避免手一抖就误存
                if (typing) Keyboard.ClearFocus();
                e.Handled = true;
                return;
            }
            if (typing) return;

            if (e.Key == Key.D1 || e.Key == Key.NumPad1) { Pick("today"); e.Handled = true; }
            else if (e.Key == Key.D2 || e.Key == Key.NumPad2) { Pick("soon"); e.Handled = true; }
            else if (e.Key == Key.D3 || e.Key == Key.NumPad3) { Pick("want"); e.Handled = true; }
        }
    }
}
