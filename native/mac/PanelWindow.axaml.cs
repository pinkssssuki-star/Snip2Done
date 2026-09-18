using System;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;

namespace RemeberEr
{
    /// <summary>
    /// 常驻面板窗口。
    ///
    /// P1 阶段只验证窗口层面的平台行为（透明 / 无边框 / 置顶 / 按物理像素定位到
    /// 右下角 / Retina 缩放换算），并把诊断信息直接画在窗口里 ——
    /// 因为 GUI 程序没有控制台，把「我以为的坐标系」和「系统实际的坐标系」同时摆在
    /// 眼前，是移植期最快的排错方式。
    /// </summary>
    public partial class PanelWindow : Window
    {
        const double PANEL_MARGIN = 20;

        TextBlock _diag;

        public PanelWindow()
        {
            Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);
            _diag = this.FindControl<TextBlock>("DiagText");

            Opened += OnOpened;
        }

        void OnOpened(object sender, EventArgs e)
        {
            PositionBottomRight();
            UpdateDiag();
            LogScreens();
            Program.Log("  [t=" + Program.Boot.ElapsedMilliseconds + "ms] 面板已显示");
            Program.Log("    位置(物理) = " + Position + " 尺寸(逻辑) = " + Width + "x" + Height);
            Program.Log("    RenderScaling = " + RenderScaling);
        }

        /// <summary>
        /// 把 Avalonia 看到的屏幕信息落进日志。
        ///
        /// 为什么值得专门写一段：抓屏拿的是**物理像素**，窗口用**逻辑像素**，
        /// 两者的换算全靠 Scaling。移植期必须能事后核对「我以为的屏幕」和
        /// 「系统实际的屏幕」是否一致 —— 只画在窗口里没法事后比对，
        /// 而 GUI 程序又没有控制台可抓。
        /// </summary>
        void LogScreens()
        {
            var screens = Screens;
            if (screens == null) { Program.Log("    !! Screens 为 null"); return; }

            Program.Log("    Avalonia 屏幕数 = " + screens.All.Count);
            int i = 0;
            foreach (var s in screens.All)
            {
                bool primary = ReferenceEquals(s, screens.Primary);
                Program.Log(string.Format("      [{0}]{1} scaling={2}", i, primary ? " 主屏" : "", s.Scaling));
                Program.Log(string.Format("         bounds(逻辑)  = {0}", s.Bounds));
                Program.Log(string.Format("         work(逻辑)    = {0}", s.WorkingArea));
                Program.Log(string.Format("         bounds(物理)  = {0}x{1} @ ({2},{3})",
                    s.Bounds.Width * s.Scaling, s.Bounds.Height * s.Scaling,
                    s.Bounds.X * s.Scaling, s.Bounds.Y * s.Scaling));
                i++;
            }
        }

        /// <summary>
        /// 放到主屏右下角。
        ///
        /// 注意换算：Avalonia 的 Window.Position 要**物理像素**，
        /// 而 Screens 给的 Bounds 是**逻辑像素** —— 混用会让窗口跑到屏幕外或错位。
        /// 这里显式用 Screen.Scaling 把逻辑换算成物理，并把过程写进诊断文本。
        /// </summary>
        void PositionBottomRight()
        {
            var screens = Screens;
            if (screens == null) return;

            var screen = screens.Primary ?? (screens.All.Count > 0 ? screens.All[0] : null);
            if (screen == null) return;

            double s = screen.Scaling > 0 ? screen.Scaling : 1.0;
            var b = screen.Bounds;          // 逻辑像素
            var wa = screen.WorkingArea;    // 逻辑像素

            // 面板自己的逻辑尺寸 -> 物理尺寸
            double wPhys = Width * s;
            double hPhys = Height * s;

            double xPhys = wa.X * s + wa.Width * s - wPhys - PANEL_MARGIN * s;
            double yPhys = wa.Y * s + wa.Height * s - hPhys - PANEL_MARGIN * s;

            Position = new Avalonia.PixelPoint((int)Math.Round(xPhys), (int)Math.Round(yPhys));
        }

        void UpdateDiag()
        {
            var sb = new StringBuilder();
            try
            {
                var screens = Screens;
                sb.AppendLine("平台: " + Program.Platform.Name);
                sb.AppendLine("Avalonia: " + typeof(Window).Assembly.GetName().Version);
                sb.AppendLine();

                if (screens != null)
                {
                    sb.AppendLine("屏幕数: " + screens.All.Count);
                    int i = 0;
                    foreach (var s in screens.All)
                    {
                        sb.AppendLine(string.Format("  [{0}]{1} scaling={2}", i,
                            ReferenceEquals(s, screens.Primary) ? " 主屏" : "", s.Scaling));
                        sb.AppendLine(string.Format("     bounds(逻辑)  {0}", s.Bounds));
                        sb.AppendLine(string.Format("     work(逻辑)    {0}", s.WorkingArea));
                        sb.AppendLine(string.Format("     bounds(物理)  {0}x{1} @ ({2},{3})",
                            s.Bounds.Width * s.Scaling, s.Bounds.Height * s.Scaling,
                            s.Bounds.X * s.Scaling, s.Bounds.Y * s.Scaling));
                        i++;
                    }
                }
                sb.AppendLine();

                sb.AppendLine("本窗口:");
                sb.AppendLine("  RenderScaling = " + RenderScaling);
                sb.AppendLine("  Position(物理) = " + Position);
                sb.AppendLine("  尺寸(逻辑) = " + Width + " x " + Height);
                sb.AppendLine("  尺寸(物理) = " + (Width * RenderScaling) + " x " + (Height * RenderScaling));
                sb.AppendLine();

                var cur = Program.Platform.GetCursorPosition();
                sb.AppendLine("光标(物理) = " + cur);
                sb.AppendLine();

                sb.AppendLine("权限:");
                sb.AppendLine("  屏幕录制 = " + (Program.Platform.HasScreenCapturePermission() ? "已授权" : "未授权"));
                sb.AppendLine("  辅助功能 = " + (Program.Platform.HasAccessibilityPermission() ? "已授权" : "未授权"));
                sb.AppendLine();

                sb.AppendLine("数据目录:");
                sb.AppendLine("  " + Program.DataDir);
                sb.AppendLine();

                var st = new Store(Program.DataDir);
                var snap = st.Snapshot();
                sb.AppendLine("数据层: 已接通");
                sb.AppendLine("  snapshot 长度 = " + (Json.Stringify(snap) ?? "").Length + " 字节");
                sb.AppendLine();

                sb.AppendLine("（平台层显示器的真假值见 native.log，");
                sb.AppendLine("  这里只画 Avalonia 自己的口径，便于两处对照）");
            }
            catch (Exception ex)
            {
                sb.AppendLine();
                sb.AppendLine("!! 诊断失败: " + ex.GetType().Name);
                sb.AppendLine(ex.Message);
            }

            if (_diag != null) _diag.Text = sb.ToString();
        }
    }
}
