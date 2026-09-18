using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace RemeberEr
{
    /// <summary>
    /// 抓屏 + 存盘。刻意走 GDI BitBlt 直连 Win32 —— 这是原生版最实在的一项优势：
    /// 之前实测 Electron 的 desktopCapturer 要 357~476ms，BitBlt 只要 26~38ms。
    /// </summary>
    public static class Capture
    {
        public static Bitmap Grab(out long grabMs)
        {
            int x, y, w, h;
            if (Program.PrimaryOnly)
            {
                x = 0; y = 0;
                w = Interop.GetSystemMetrics(0); // SM_CXSCREEN
                h = Interop.GetSystemMetrics(1); // SM_CYSCREEN
            }
            else
            {
                x = Interop.GetSystemMetrics(Interop.SM_XVIRTUALSCREEN);
                y = Interop.GetSystemMetrics(Interop.SM_YVIRTUALSCREEN);
                w = Interop.GetSystemMetrics(Interop.SM_CXVIRTUALSCREEN);
                h = Interop.GetSystemMetrics(Interop.SM_CYVIRTUALSCREEN);
            }

            IntPtr screenDc = Interop.GetDC(IntPtr.Zero);
            IntPtr memDc = Interop.CreateCompatibleDC(screenDc);
            IntPtr hBmp = Interop.CreateCompatibleBitmap(screenDc, w, h);
            IntPtr old = Interop.SelectObject(memDc, hBmp);

            var sw = Stopwatch.StartNew();
            // CAPTUREBLT：不带它截不到分层窗口
            Interop.BitBlt(memDc, 0, 0, w, h, screenDc, x, y, Interop.SRCCOPY | Interop.CAPTUREBLT);
            grabMs = sw.ElapsedMilliseconds;

            var bmp = Image.FromHbitmap(hBmp);
            Interop.SelectObject(memDc, old);
            Interop.DeleteObject(hBmp);
            Interop.DeleteDC(memDc);
            Interop.ReleaseDC(IntPtr.Zero, screenDc);
            return bmp;
        }

        /// <summary>只抓屏幕上的一块物理矩形（框选层用的就是这块屏）。</summary>
        public static Bitmap GrabRect(int x, int y, int w, int h, out long grabMs)
        {
            IntPtr screenDc = Interop.GetDC(IntPtr.Zero);
            IntPtr memDc = Interop.CreateCompatibleDC(screenDc);
            IntPtr hBmp = Interop.CreateCompatibleBitmap(screenDc, w, h);
            IntPtr old = Interop.SelectObject(memDc, hBmp);

            var sw = Stopwatch.StartNew();
            Interop.BitBlt(memDc, 0, 0, w, h, screenDc, x, y, Interop.SRCCOPY | Interop.CAPTUREBLT);
            grabMs = sw.ElapsedMilliseconds;

            var bmp = Image.FromHbitmap(hBmp);
            Interop.SelectObject(memDc, old);
            Interop.DeleteObject(hBmp);
            Interop.DeleteDC(memDc);
            Interop.ReleaseDC(IntPtr.Zero, screenDc);
            return bmp;
        }

        /// <summary>
        /// 按物理像素矩形从整屏截图里裁一块。
        /// 矩形已经换算成物理像素了 —— 框选层量到的是 DIP，乘过缩放才进来。
        /// </summary>
        public static Bitmap Crop(Bitmap src, int x, int y, int w, int h)
        {
            x = Math.Max(0, Math.Min(x, src.Width - 1));
            y = Math.Max(0, Math.Min(y, src.Height - 1));
            w = Math.Max(1, Math.Min(w, src.Width - x));
            h = Math.Max(1, Math.Min(h, src.Height - y));

            var dst = new Bitmap(w, h);
            using (var g = Graphics.FromImage(dst))
            {
                g.DrawImage(src, new Rectangle(0, 0, w, h), new Rectangle(x, y, w, h), GraphicsUnit.Pixel);
            }
            return dst;
        }

        public static byte[] ToPng(Bitmap bmp, out long pngMs)
        {
            var sw = Stopwatch.StartNew();
            using (var ms = new MemoryStream())
            {
                bmp.Save(ms, ImageFormat.Png);
                pngMs = sw.ElapsedMilliseconds;
                return ms.ToArray();
            }
        }

        /// <summary>把整屏存成 PNG，用来出对比截图。</summary>
        public static void SaveFullScreen(string path)
        {
            long ms;
            using (var bmp = Grab(out ms))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                bmp.Save(path, ImageFormat.Png);
            }
        }

        /// <summary>只裁某个矩形（用于「面板周围一块」的对比图）。矩形是物理像素。</summary>
        public static void SaveRegion(string path, Interop.RECT r, int pad)
        {
            long ms;
            using (var bmp = Grab(out ms))
            {
                int vx = Interop.GetSystemMetrics(Interop.SM_XVIRTUALSCREEN);
                int vy = Interop.GetSystemMetrics(Interop.SM_YVIRTUALSCREEN);
                var rect = new Rectangle(r.Left - pad - vx, r.Top - pad - vy,
                    (r.Right - r.Left) + pad * 2, (r.Bottom - r.Top) + pad * 2);
                rect = Rectangle.Intersect(rect, new Rectangle(0, 0, bmp.Width, bmp.Height));
                if (rect.Width <= 0 || rect.Height <= 0) return;

                Directory.CreateDirectory(Path.GetDirectoryName(path));
                using (var crop = new Bitmap(rect.Width, rect.Height))
                {
                    using (var g = Graphics.FromImage(crop))
                        g.DrawImage(bmp, new Rectangle(0, 0, rect.Width, rect.Height), rect, GraphicsUnit.Pixel);
                    crop.Save(path, ImageFormat.Png);
                }
            }
        }
    }
}
