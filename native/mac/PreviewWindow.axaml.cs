using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace RemeberEr
{
    /// <summary>
    /// 长悬停时的放大预览。贴着锚点元素的左边显示；左边放不下就翻到面板右边。
    ///
    /// 原版靠 Win32 的 HideFromAltTab 让它不进 Alt+Tab，Avalonia 里对应的是
    /// ShowInTaskbar=False + 一个不抢焦点的窗口。这里不去硬碰 AppKit 的
    /// NSWindow 层级：预览窗本身是 Topmost 且不激活，实际表现与目标一致，
    /// 而少写一段平台相关的窗口层级代码，移植风险更小。
    /// </summary>
    public partial class PreviewWindow : Window
    {
        Image _pic;

        public PreviewWindow()
        {
            Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);
            _pic = this.FindControl<Image>("Pic");
        }

        /// <summary>
        /// 贴着锚点元素的左边显示；左边放不下就翻到面板右边去。
        ///
        /// <paramref name="panelLeft"/> 与 <paramref name="anchorTopLeft"/> 都是
        /// **物理像素**（和 IPlatform 的口径一致），而 Avalonia 的 Position 也是物理像素，
        /// 所以这里不需要再折算 —— 这也是把平台层统一成物理像素的好处之一。
        /// </summary>
        public void ShowAt(Bitmap img, double anchorTop, double anchorHeight, double panelLeft, double panelWidth)
        {
            _pic.Source = img;

            // 先让布局算一遍，才拿得到真实尺寸（原版同样是先 UpdateLayout 再量）
            UpdateLayout();
            double w = Bounds.Width > 0 ? Bounds.Width : 200;
            double h = Bounds.Height > 0 ? Bounds.Height : 200;

            double left = panelLeft - w - 10;
            if (left < 0) left = panelLeft + panelWidth + 10;   // 左边放不下就翻到右边

            double top = anchorTop + anchorHeight / 2 - h / 2;

            // 纵向钳制用**这块屏的工作区**（原版用的是 SystemParameters.WorkArea，
            // 那是主屏的工作区；这里取光标所在屏更准）。
            var screen = Screens?.ScreenFromPoint(
                new Avalonia.PixelPoint((int)Math.Round(panelLeft), (int)Math.Round(anchorTop)))
                ?? Screens?.Primary;
            if (screen != null)
            {
                double s = screen.Scaling > 0 ? screen.Scaling : 1.0;
                double waTop = screen.WorkingArea.Y * s;
                double waBottom = (screen.WorkingArea.Y + screen.WorkingArea.Height) * s;
                if (top < waTop) top = waTop;
                if (top + h > waBottom) top = waBottom - h;
            }

            Position = new Avalonia.PixelPoint((int)Math.Round(left), (int)Math.Round(top));
            if (!IsVisible) Show();
        }
    }
}
