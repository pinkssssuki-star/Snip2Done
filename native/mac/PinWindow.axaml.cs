using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;

namespace RemeberEr
{
    /// <summary>
    /// 贴图窗口：把截下来的一块钉在屏幕上（对应 Snipaste 的贴图）。
    ///
    /// 与「存起来」是两条完全不同的路：
    ///   ✓ 存起来 —— 问 Todo/Wantdo、带期限、进面板、参与衰减
    ///   📌 贴图   —— 什么都不记，就是一张摆在那儿的图，关掉就没了
    /// 所以它**不写任何数据**，连 Store 都不碰。
    ///
    /// 交互照着 Snipaste 的习惯做：
    ///   · 拖动 = 移动（无边框窗口要自己实现）
    ///   · 滚轮 = 缩放（以光标位置为锚点，缩放时不会跑偏）
    ///   · 双击 / Esc / 右键 = 关掉
    /// </summary>
    public partial class PinWindow : Window
    {
        readonly Bitmap _img;
        readonly double _baseW, _baseH;
        double _scale = 1.0;

        Image _pic;

        const double MIN_SCALE = 0.15;
        const double MAX_SCALE = 8.0;

        public PinWindow(Bitmap img, int physicalW, int physicalH, double screenScale, double logicalX, double logicalY)
        {
            Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);
            _img = img;
            _pic = this.FindControl<Image>("Pic");

            _pic.Source = img;

            // 窗口尺寸用**逻辑单位**（物理像素 ÷ 屏幕缩放）。
            // 抓屏拿到的是物理像素，而窗口尺寸是逻辑像素 —— 混用会让贴图在
            // Retina 屏上变成两倍大（这个坑项目里踩过好几次，处理方式统一成「先换算再用」）。
            double s = screenScale > 0 ? screenScale : 1.0;
            _baseW = physicalW / s;
            _baseH = physicalH / s;
            Width = _baseW;
            Height = _baseH;

            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint(
                (int)Math.Round(logicalX * s),
                (int)Math.Round(logicalY * s));

            // 拖动：无边框窗口没有系统标题栏，得自己接
            PointerPressed += OnPointerPressed;

            // 滚轮缩放：以光标为锚点
            PointerWheelChanged += OnWheel;

            // 双击关掉
            DoubleTapped += (_, __) => Close();

            KeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape) { e.Handled = true; Close(); }
            };

            Opened += (_, __) =>
            {
                // 贴图要浮在普通窗口之上，但不抢焦点（不想打断用户正在做的事）
                try
                {
                    var h = TryGetPlatformHandle();
                    if (h != null && h.Handle != IntPtr.Zero)
                        OverlayBridge.remeber_window_make_panel(h.Handle);
                }
                catch (Exception) { }

                Program.Log(string.Format("  贴图已钉住：{0}x{1}（逻辑 {2:0}x{3:0}）@ {4}",
                    physicalW, physicalH, _baseW, _baseH, Position));
            };
        }

        void OnPointerPressed(object sender, PointerPressedEventArgs e)
        {
            var p = e.GetCurrentPoint(this);

            // 右键 = 关掉（和框选层/归类窗的「右键取消」同一个约定）
            if (p.Properties.IsRightButtonPressed) { e.Handled = true; Close(); return; }

            if (!p.Properties.IsLeftButtonPressed) return;
            try { BeginMoveDrag(e); }
            catch (Exception ex) { Program.Log("  贴图拖动失败：" + ex.Message); }
        }

        /// <summary>
        /// 滚轮缩放，以**光标位置为锚点** —— 不这么做的话缩放时图会往左上角跑，
        /// 想放大右下角的内容就得一边滚一边追着拖窗口。
        /// </summary>
        void OnWheel(object sender, PointerWheelEventArgs e)
        {
            double factor = e.Delta.Y > 0 ? 1.1 : (1.0 / 1.1);
            double next = Math.Max(MIN_SCALE, Math.Min(MAX_SCALE, _scale * factor));
            if (Math.Abs(next - _scale) < 0.0001) return;

            // 光标在窗口内的位置（逻辑单位）
            var pos = e.GetPosition(this);
            double relX = Width > 0 ? pos.X / Width : 0.5;
            double relY = Height > 0 ? pos.Y / Height : 0.5;

            // 按同样的相对位置反推新的左上角，光标底下那个点就保持不动
            double newW = _baseW * next;
            double newH = _baseH * next;
            double sx, sy;
            Dpi.Scale(this, out sx, out sy);

            double newLeftPhys = Position.X + (relX * Width - relX * newW) * sx;
            double newTopPhys = Position.Y + (relY * Height - relY * newH) * sy;

            _scale = next;
            Width = newW;
            Height = newH;
            Position = new PixelPoint((int)Math.Round(newLeftPhys), (int)Math.Round(newTopPhys));

            e.Handled = true;
        }
    }
}
