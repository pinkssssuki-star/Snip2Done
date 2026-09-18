using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace RemeberEr
{
    /// <summary>
    /// 长悬停时在面板左边弹出的放大预览。和 Electron 版一样是一扇独立的无边框窗口，
    /// 不抢焦点、不进 Alt+Tab、不接收鼠标。
    /// </summary>
    public partial class PreviewWindow : Window
    {
        public PreviewWindow()
        {
            InitializeComponent();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var h = new WindowInteropHelper(this).Handle;
            Interop.HideFromAltTab(h);
        }

        /// <summary>贴着锚点元素的左边显示；右边放不下就翻到左边去。</summary>
        public void ShowAt(ImageSource img, Point anchorTopLeft, double anchorHeight, double panelLeft)
        {
            Pic.Source = img;

            // 先让布局算一遍，才拿得到真实尺寸
            UpdateLayout();
            double w = ActualWidth > 0 ? ActualWidth : 200;
            double h = ActualHeight > 0 ? ActualHeight : 200;

            double left = panelLeft - w - 10;
            if (left < 0) left = panelLeft + 340 + 10;   // 左边放不下就翻到右边
            double top = anchorTopLeft.Y + anchorHeight / 2 - h / 2;
            var wa = SystemParameters.WorkArea;
            if (top < wa.Top) top = wa.Top;
            if (top + h > wa.Bottom) top = wa.Bottom - h;

            Left = left;
            Top = top;
            if (!IsVisible) Show();
        }
    }
}
