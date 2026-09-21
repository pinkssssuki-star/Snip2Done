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
        int _wheelCount;
        void OnWheel(object sender, PointerWheelEventArgs e)
        {
            // 诊断：Mos 之类的滚轮插件会重写事件，先看清送来的 delta 长什么样
            if (_wheelCount < 12)
            {
                _wheelCount++;
                var pp = e.GetPosition(this);
                Program.Log(string.Format(
                    "  [缩放] delta=({0:0.###},{1:0.###}) 光标在窗口内=({2:0.#},{3:0.#}) 当前={4:0.#}x{5:0.#} pos={6} scale={7:0.###}",
                    e.Delta.X, e.Delta.Y, pp.X, pp.Y, Width, Height, Position, _scale));
            }

            double factor = e.Delta.Y > 0 ? 1.1 : (1.0 / 1.1);
            double next = Math.Max(MIN_SCALE, Math.Min(MAX_SCALE, _scale * factor));
            if (Math.Abs(next - _scale) < 0.0001) return;

            // 光标在本窗口内的位置，除以**本窗口自己的尺寸**得到相对位置。
            // 两者都取同一套坐标（窗口坐标系），比之前拿「窗口坐标 ÷ 内容尺寸」自洽。
            var pos = e.GetPosition(this);
            double curW = Bounds.Width > 0 ? Bounds.Width : Width;
            double curH = Bounds.Height > 0 ? Bounds.Height : Height;
            if (curW <= 0 || curH <= 0) { e.Handled = true; return; }

            double relX = Math.Max(0, Math.Min(1, pos.X / curW));
            double relY = Math.Max(0, Math.Min(1, pos.Y / curH));

            double newW = _baseW * next;
            double newH = _baseH * next;

            // 保持「光标底下那个点」不动：
            //   光标到窗口左边的距离，缩放前后应当同比例
            //   relX * curW  →  relX * newW
            // 差多少就把窗口左上角挪多少。
            double dxLog = relX * (curW - newW);
            double dyLog = relY * (curH - newH);

            double sx, sy;
            Dpi.Scale(this, out sx, out sy);
            double newLeftPhys = Position.X + dxLog * sx;
            double newTopPhys = Position.Y + dyLog * sy;

            _scale = next;

            // 位置和尺寸**一次设完**。
            //
            // 分两步（先 Width/Height 再 Position）的话，macOS 会在两次调用之间
            // 重新布局、可能插一段动画，于是「以光标为锚点」的推算对不上实际状态 ——
            // 表现就是缩放时图会漂、而且一卡一卡。
            bool moved = false;
            try
            {
                var h = TryGetPlatformHandle();
                if (h != null && h.Handle != IntPtr.Zero)
                {
                    OverlayBridge.remeber_window_set_frame(h.Handle, newLeftPhys, newTopPhys,
                        newW * sx, newH * sy);
                    moved = true;
                }
            }
            catch (Exception) { }

            if (!moved)
            {
                // 拿不到原生句柄时退回托管路径（位置可能略有误差，但功能正常）
                Width = newW;
                Height = newH;
                Position = new PixelPoint((int)Math.Round(newLeftPhys), (int)Math.Round(newTopPhys));
            }

            e.Handled = true;
        }
    }
}
