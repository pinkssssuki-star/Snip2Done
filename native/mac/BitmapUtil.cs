using System;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace RemeberEr
{
    /// <summary>
    /// 把平台层抓到的 BGRA 原始像素转成 Avalonia 能画的位图。
    ///
    /// 为什么需要这一层：平台层刻意只吐「BGRA 字节 + 尺寸」这种最中立的形式
    /// （和 Win32 版 BitBlt 的口径一致），不依赖任何 UI 框架；
    /// 而 Avalonia 要的是 Bitmap/WriteableBitmap。转换只在这里发生一次。
    /// </summary>
    public static class BitmapUtil
    {
        /// <summary>
        /// 抓到的 BGRA 缓冲 → Avalonia 位图。
        ///
        /// 关键点是像素格式要写成 Bgra8888：平台层已经保证缓冲是 BGRA
        /// （见 MacPlatform.GrabDisplay 里对 CGBitmapContext 的格式指定），
        /// 两边必须对齐，否则会出现红蓝互换。
        /// </summary>
        public static WriteableBitmap ToBitmap(Screenshot shot)
        {
            if (shot == null || shot.Bgra == null || shot.Width <= 0 || shot.Height <= 0) return null;

            var wb = new WriteableBitmap(
                new PixelSize(shot.Width, shot.Height),
                new Vector(96, 96),
                PixelFormat.Bgra8888,
                AlphaFormat.Premul);

            using (var fb = wb.Lock())
            {
                var src = shot.Bgra;
                int srcRowBytes = shot.Width * 4;
                unsafe
                {
                    var dstBase = (byte*)fb.Address;
                    for (int y = 0; y < shot.Height; y++)
                    {
                        var dst = dstBase + y * fb.RowBytes;
                        System.Runtime.InteropServices.Marshal.Copy(
                            src, y * srcRowBytes, (IntPtr)dst, srcRowBytes);
                    }
                }
            }

            return wb;
        }
    }
}
