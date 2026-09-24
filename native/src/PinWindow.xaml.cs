using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RemeberEr
{
    /// <summary>
    /// 钉在屏幕上的一张截图。
    ///
    /// 和 Snipaste 的「贴图」一个意思：截完框选，点图钉，这块图就浮在桌面上，
    /// 一直压在别的窗口上面，直到你关掉它。**不落库** —— 想要变成备忘就点勾，
    /// 想临时摆在眼前看就点图钉，两条路互不干扰。
    ///
    /// 操作：拖任意处移动 · 拖边角缩放 · 滚轮缩放 · 双击关掉 · Esc 关掉 · 右上角 ✕
    /// </summary>
    public partial class PinWindow : Window
    {
        readonly BitmapSource _img;
        readonly int _natW, _natH;      // 原图像素尺寸
        readonly double _scale;         // 截图那块屏的 DPI 缩放

        public PinWindow(BitmapSource img, int screenX, int screenY, double scale)
        {
            InitializeComponent();
            _img = img;
            _natW = img.PixelWidth;
            _natH = img.PixelHeight;
            _scale = scale > 0 ? scale : 1.0;
            Pic.Source = img;

            // 初始尺寸：按原图在**该屏上**的物理大小，太大就收到屏幕的 70%
            Interop.RECT work;
            int maxW = 800, maxH = 600;
            if (Interop.WorkAreaAt(screenX, screenY, out work))
            {
                maxW = (int)((work.Right - work.Left) * 0.7);
                maxH = (int)((work.Bottom - work.Top) * 0.7);
            }

            int w = _natW, h = _natH;
            if (w > maxW) { h = (int)Math.Round(h * (double)maxW / w); w = maxW; }
            if (h > maxH) { w = (int)Math.Round(w * (double)maxH / h); h = maxH; }

            // 摆到选区原来的位置（就被钉在那块地方），再夹回工作区
            int px = screenX, py = screenY;
            if (work.Right > work.Left)
            {
                px = Math.Max(work.Left, Math.Min(px, work.Right - w));
                py = Math.Max(work.Top, Math.Min(py, work.Bottom - h));
            }

            // 和别的窗口一样：出生前就按目标屏的 DPI 算好逻辑坐标
            Left = px / _scale;
            Top = py / _scale;
            Width = w / _scale;
            Height = h / _scale;
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var h = new WindowInteropHelper(this).Handle;
            Interop.EnableDwmChrome(h);       // 圆角 + 投影
            Interop.HideFromAltTab(h);        // 贴图不该出现在 Alt+Tab 里
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

        void OnClose(object sender, RoutedEventArgs e) { Close(); }

        void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            Close();
        }

        /// <summary>滚轮缩放：按物理像素等比改窗口大小，夹在 10%~400% 之间。</summary>
        void OnWheel(object sender, MouseWheelEventArgs e)
        {
            e.Handled = true;
            double step = e.Delta > 0 ? 1.1 : 1 / 1.1;
            double w = ActualWidth * step, h = ActualHeight * step;

            double minW = 60 / _scale, maxW = _natW * 4 / _scale;
            if (w < minW || w > maxW) return;

            // 让缩放围绕鼠标位置，手感和看图软件一致
            var p = e.GetPosition(this);
            double fx = p.X / Math.Max(1, ActualWidth), fy = p.Y / Math.Max(1, ActualHeight);
            double newLeft = Left + (ActualWidth - w) * fx;
            double newTop = Top + (ActualHeight - h) * fy;

            Width = w; Height = h;
            Left = newLeft; Top = newTop;
        }

        void OnHover(object sender, MouseEventArgs e)
        {
            bool on = IsMouseOver;
            CloseBtn.Opacity = on ? 1 : 0;
            PinHint.Opacity = on ? 1 : 0;
            Edge.BorderBrush = on ? new SolidColorBrush(Color.FromArgb(0x66, 0x8b, 0x5c, 0xf6))
                                  : new SolidColorBrush(Color.FromArgb(0x33, 0, 0, 0));
        }
    }
}
