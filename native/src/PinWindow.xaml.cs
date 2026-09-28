using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace RemeberEr
{
    /// <summary>
    /// 钉在屏幕上的一张截图（Snipaste 的「贴图」）。
    ///
    /// 它**就是一张图** —— 无边框、无圆角、无关闭键。操作只有三个：
    /// 拖任意处移动 · 滚轮或拖边缩放 · 双击 / Esc 关掉。
    ///
    /// 不落库：想要变成备忘就点框选层那个勾，想临时摆在眼前参考就点图钉，两条路分开。
    /// </summary>
    public partial class PinWindow : Window
    {
        readonly int _natW, _natH;      // 原图像素尺寸
        readonly double _scale;         // 截图那块屏的 DPI 缩放
        Interop.RECT _work;             // 所在屏的工作区（物理像素）
        bool _haveWork;

        public PinWindow(BitmapSource img, int screenX, int screenY, double scale)
        {
            InitializeComponent();
            _natW = img.PixelWidth;
            _natH = img.PixelHeight;
            _scale = scale > 0 ? scale : 1.0;
            Pic.Source = img;

            Interop.RECT work;
            _haveWork = Interop.WorkAreaAt(screenX + 1, screenY + 1, out work);
            _work = work;

            // **默认 1:1（物理像素原样）** —— 之前按「屏幕的 70%」封顶，
            // 大图会被缩着摆，看起来就是「画质有损失」。只有实在放不下才收一点。
            int w = _natW, h = _natH;
            if (_haveWork)
            {
                int maxW = work.Right - work.Left, maxH = work.Bottom - work.Top;
                if (w > maxW || h > maxH)
                {
                    double k = Math.Min((double)maxW / w, (double)maxH / h);
                    w = Math.Max(1, (int)Math.Round(w * k));
                    h = Math.Max(1, (int)Math.Round(h * k));
                }
            }

            int px = screenX, py = screenY;
            if (_haveWork)
            {
                px = Math.Max(work.Left, Math.Min(px, work.Right - w));
                py = Math.Max(work.Top, Math.Min(py, work.Bottom - h));
            }

            // 出生前就按目标屏的 DPI 算好逻辑坐标（混 DPI 下不这么做位置和比例都会错）
            Left = px / _scale;
            Top = py / _scale;
            Width = w / _scale;
            Height = h / _scale;
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var h = new WindowInteropHelper(this).Handle;
            Interop.SetSquareCorners(h);   // 直角（Win11 默认给圆角，得显式关）
            Interop.SetNoBorder(h);        // 去掉那条细边 —— 用户要的就是「一张无边框的图」
            Interop.HideFromAltTab(h);     // 贴图不该出现在 Alt+Tab 里
        }

        void OnDragArea(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;
            try { DragMove(); } catch (Exception) { }
        }

        void OnDoubleClick(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            Close();
        }

        void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            Close();
        }

        /// <summary>
        /// 滚轮缩放。
        ///
        /// **一次 SetWindowPos 把位置和尺寸一起改掉**，不去碰 WPF 的
        /// Width/Height/Left/Top —— 那是四个属性、四次独立的窗口重排，
        /// 中间会看到窗口先变大再挪位的抽动（用户看到的「频闪」就是这么来的）。
        /// </summary>
        void OnWheel(object sender, MouseWheelEventArgs e)
        {
            e.Handled = true;

            var hwnd = new WindowInteropHelper(this).Handle;
            Interop.RECT r;
            Interop.GetWindowRect(hwnd, out r);
            int curW = r.Right - r.Left, curH = r.Bottom - r.Top;
            if (curW < 1 || curH < 1) return;

            double step = e.Delta > 0 ? 1.12 : 1 / 1.12;
            int w = (int)Math.Round(curW * step);
            int h = (int)Math.Round(curH * step);

            int minW = (int)Math.Round(40 * _scale), maxW = _natW * 6;
            if (w < minW || w > maxW) return;

            // 围绕鼠标位置缩放，手感和看图软件一致
            var p = e.GetPosition(this);
            double fx = p.X / Math.Max(1, ActualWidth), fy = p.Y / Math.Max(1, ActualHeight);
            int nx = r.Left + (int)Math.Round((curW - w) * fx);
            int ny = r.Top + (int)Math.Round((curH - h) * fy);

            Interop.SetWindowPos(hwnd, IntPtr.Zero, nx, ny, w, h,
                Interop.SWP_NOZORDER | Interop.SWP_NOACTIVATE | Interop.SWP_NOCOPYBITS);
        }
    }
}
