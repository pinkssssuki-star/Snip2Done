using System;
using System.Runtime.InteropServices;

namespace RemeberEr
{
    /// <summary>
    /// macOS 原生 API 声明（CoreGraphics / AppKit / Carbon / Accessibility）。
    ///
    /// 这是 WPF 版 Interop.cs 的对应物，但只保留**声明**，
    /// 具体能力编排放在 MacPlatform.cs 里。
    ///
    /// 坐标口径（移植期最容易搞错的地方，先记在这里）：
    ///   - CGDisplayBounds 返回的是**全局显示坐标系**，原点在主屏左上角，y 向下 ——
    ///     和 Win32 的虚拟屏坐标语义一致，所以可以直接沿用原版的算法。
    ///   - 但 AppKit（NSScreen / NSWindow）用的是**原点左下角、y 向上**的坐标系。
    ///     凡是要和 NSWindow 交互的地方（移动窗口、设层级）都必须翻转 y：
    ///         y_appkit = mainDisplayHeight - y_cg - height
    ///     参考：Apple 文档 "Drawing and Printing Guide" 的 Coordinate Systems 一节。
    ///   - CGDisplayBounds 的单位是**点（逻辑像素）**，不是物理像素。
    ///     要拿物理像素得乘 CGDisplayPixelsWide / CGDisplayBounds.Width 的比值。
    ///     原版 WPF 也有同样的物理/逻辑之分，处理思路一致。
    /// </summary>
    internal static class MacInterop
    {
        // ------------------------------------------------------------ CoreGraphics

        internal const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";

        [StructLayout(LayoutKind.Sequential)]
        internal struct CGPoint
        {
            public double X;
            public double Y;
            public CGPoint(double x, double y) { X = x; Y = y; }
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct CGSize
        {
            public double Width;
            public double Height;
            public CGSize(double w, double h) { Width = w; Height = h; }
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct CGRect
        {
            public CGPoint Origin;
            public CGSize Size;
            public CGRect(double x, double y, double w, double h)
            {
                Origin = new CGPoint(x, y);
                Size = new CGSize(w, h);
            }
            public double X { get { return Origin.X; } }
            public double Y { get { return Origin.Y; } }
            public double Width { get { return Size.Width; } }
            public double Height { get { return Size.Height; } }
        }

        internal const int kCGErrorSuccess = 0;

        [DllImport(CoreGraphics)]
        internal static extern int CGGetActiveDisplayList(uint maxDisplays, uint[] activeDisplays, out uint displayCount);

        [DllImport(CoreGraphics)]
        internal static extern CGRect CGDisplayBounds(uint display);

        [DllImport(CoreGraphics)]
        internal static extern uint CGMainDisplayID();

        [DllImport(CoreGraphics)]
        internal static extern bool CGDisplayIsMain(uint display);

        [DllImport(CoreGraphics)]
        internal static extern nuint CGDisplayPixelsWide(uint display);

        [DllImport(CoreGraphics)]
        internal static extern nuint CGDisplayPixelsHigh(uint display);

        /// <summary>鼠标位置，单位是**全局显示坐标系的点**（原点左上，y 向下）。</summary>
        [DllImport(CoreGraphics)]
        internal static extern CGPoint CGEventGetLocation(IntPtr @event);

        // ------------------------------------------------------------ AppKit
        //
        // 目前不需要 AppKit 的声明：显示器几何、缩放比、鼠标位置都能从 CoreGraphics 拿到。
        // 真正需要 AppKit 的是 P3/P4 的窗口层级与贴边隐藏（NSWindow.setLevel: 等），
        // 到那时再从 Avalonia 的 macOS 后端取 NSWindow 句柄，在这里补声明。
        // 刻意先不写没有用到的 P/Invoke —— 声明了却没用过的 native 方法最容易腐烂。

        // ------------------------------------------------------------ 位图与上下文
        //
        // 抓屏走「画进自己建的 CGBitmapContext」这条路，而不是直接读
        // CGImage 的 provider 数据。原因：provider 的像素格式和字节序要看
        // 系统内部怎么给的，CGImageGetBitmapInfo 在不同版本/不同源上并不稳定；
        // 而自己建上下文时**格式由我们指定**，结果确定，也方便和 Win32 版
        // 「BGRA 直出」的口径对齐。

        internal const uint kCGImageAlphaPremultipliedFirst = 2;
        internal const uint kCGBitmapByteOrder32Little = 2 << 12;
        internal const int kCGRenderingIntentDefault = 0;
        internal const int kCGColorSpaceSRGB = 0;
        internal const uint kCGWindowListOptionOnScreenOnly = 1;
        internal const int kCGNullWindowID = 0;

        [DllImport(CoreGraphics)]
        internal static extern IntPtr CGDisplayCreateImage(uint display);

        [DllImport(CoreGraphics)]
        internal static extern IntPtr CGWindowListCreateImage(CGRect screenBounds, uint listOption, int windowID, uint imageOption);

        [DllImport(CoreGraphics)]
        internal static extern nuint CGImageGetWidth(IntPtr image);

        [DllImport(CoreGraphics)]
        internal static extern nuint CGImageGetHeight(IntPtr image);

        [DllImport(CoreGraphics)]
        internal static extern IntPtr CGColorSpaceCreateDeviceRGB();

        [DllImport(CoreGraphics)]
        internal static extern IntPtr CGBitmapContextCreate(
            IntPtr data, nuint width, nuint height, nuint bitsPerComponent,
            nuint bytesPerRow, IntPtr colorSpace, uint bitmapInfo);

        [DllImport(CoreGraphics)]
        internal static extern void CGContextDrawImage(IntPtr context, CGRect rect, IntPtr image);

        [DllImport(CoreGraphics)]
        internal static extern void CGContextSetBlendMode(IntPtr context, int mode);

        [DllImport(CoreGraphics)]
        internal static extern void CGContextSetRGBFillColor(IntPtr context, double r, double g, double b, double a);

        [DllImport(CoreGraphics)]
        internal static extern void CGContextFillRect(IntPtr context, CGRect rect);

        [DllImport(CoreGraphics)]
        internal static extern void CGContextRelease(IntPtr context);

        internal const int kCGBlendModeNormal = 0;
        internal const int kCGBlendModeCopy = 17;

        // ------------------------------------------------------------ PNG 编码
        //
        // 用 ImageIO 的 CGImageDestination 写 PNG，而不是自己拉一个图像库：
        // 官方框架、无第三方依赖（和项目「零第三方依赖」的原则一致），
        // 而且它直接接受我们已经排好的 BGRA 缓冲，不需要中间格式转换。

        internal const string ImageIO = "/System/Library/Frameworks/ImageIO.framework/ImageIO";

        [DllImport(ImageIO)]
        internal static extern IntPtr CGImageDestinationCreateWithURL(IntPtr url, IntPtr type, nuint count, IntPtr options);

        [DllImport(ImageIO)]
        internal static extern void CGImageDestinationAddImage(IntPtr dest, IntPtr image, IntPtr properties);

        [DllImport(ImageIO)]
        internal static extern bool CGImageDestinationFinalize(IntPtr dest);

        // 下面这几行原来是查 kUTTypePNG（public.png）。
        // 现在直接用字符串造 CFString，所以 MobileCoreServices/CoreServices 的声明都删掉了
        // —— 声明了却没用过的 native 方法最容易腐烂，而且 MobileCoreServices 在新系统上
        // 已被并入 CoreServices，路径本身也不可靠。

        // CFURLCreateWithFileSystemPath
        [DllImport(CoreFoundation)]
        internal static extern IntPtr CFURLCreateWithFileSystemPath(
            IntPtr allocator, IntPtr filePath, int pathStyle, bool isDirectory);

        internal const int kCFURLPOSIXPathStyle = 0;

        /// <summary>
        /// 把 BGRA 缓冲写成 PNG。
        /// 先造一个 CGImage 包住缓冲，再交给 ImageIO 写盘。
        /// 返回是否成功；失败只记日志，不抛（调用方通常是「顺手存个证据」的场景）。
        /// </summary>
        internal static bool WritePng(string path, int width, int height, byte[] bgra)
        {
            if (string.IsNullOrEmpty(path) || bgra == null || width <= 0 || height <= 0) return false;

            IntPtr cs = IntPtr.Zero, provider = IntPtr.Zero, image = IntPtr.Zero;
            IntPtr data = IntPtr.Zero, url = IntPtr.Zero, type = IntPtr.Zero, dest = IntPtr.Zero;
            GCHandle handle = default(GCHandle);
            try
            {
                cs = CGColorSpaceCreateDeviceRGB();
                uint info = kCGImageAlphaPremultipliedFirst | kCGBitmapByteOrder32Little;

                handle = GCHandle.Alloc(bgra, GCHandleType.Pinned);
                provider = CGDataProviderCreateWithData(IntPtr.Zero, handle.AddrOfPinnedObject(),
                    (nuint)bgra.Length, IntPtr.Zero);
                if (provider == IntPtr.Zero) return false;

                image = CGImageCreate((nuint)width, (nuint)height, 8, 32, (nuint)(width * 4),
                    cs, info, provider, IntPtr.Zero, false, kCGRenderingIntentDefault);
                if (image == IntPtr.Zero) return false;

                var pathStr = MakeCFString(path);
                url = CFURLCreateWithFileSystemPath(IntPtr.Zero, pathStr, kCFURLPOSIXPathStyle, false);
                if (pathStr != IntPtr.Zero) CFRelease(pathStr);
                if (url == IntPtr.Zero) return false;

                type = MakeCFString("public.png");
                if (type == IntPtr.Zero) return false;

                dest = CGImageDestinationCreateWithURL(url, type, 1, IntPtr.Zero);
                if (dest == IntPtr.Zero) return false;

                CGImageDestinationAddImage(dest, image, IntPtr.Zero);
                return CGImageDestinationFinalize(dest);
            }
            catch (Exception ex)
            {
                Program.Log("!! 写 PNG 失败：" + ex.Message);
                return false;
            }
            finally
            {
                if (dest != IntPtr.Zero) CFRelease(dest);
                if (type != IntPtr.Zero) CFRelease(type);
                if (url != IntPtr.Zero) CFRelease(url);
                if (image != IntPtr.Zero) CFRelease(image);
                if (provider != IntPtr.Zero) CFRelease(provider);
                if (cs != IntPtr.Zero) CFRelease(cs);
                if (handle.IsAllocated) handle.Free();
                if (data != IntPtr.Zero) CFRelease(data);
            }
        }

        [DllImport(CoreGraphics)]
        internal static extern IntPtr CGDataProviderCreateWithData(IntPtr info, IntPtr data, nuint size, IntPtr releaseData);

        [DllImport(CoreGraphics)]
        internal static extern IntPtr CGImageCreate(
            nuint width, nuint height, nuint bitsPerComponent, nuint bitsPerPixel, nuint bytesPerRow,
            IntPtr colorSpace, uint bitmapInfo, IntPtr provider, IntPtr decode,
            bool shouldInterpolate, int intent);

        // ------------------------------------------------------------ 权限

        [DllImport(CoreGraphics)]
        internal static extern bool CGPreflightScreenCaptureAccess();

        [DllImport(CoreGraphics)]
        internal static extern bool CGRequestScreenCaptureAccess();

        // ------------------------------------------------------------ Accessibility
        //
        // kAXTrustedCheckOptionPrompt 的真实取值是 CFStringRef "AXTrustedCheckOptionPrompt"。
        // 用 CFStringCreateWithCString 现造一个，比硬编码一个指针可靠
        // （CFString 是 toll-free bridged 的，但内部布局不是公开契约）。

        internal const string ApplicationServices =
            "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";

        [DllImport(ApplicationServices)]
        internal static extern bool AXIsProcessTrusted();

        [DllImport(ApplicationServices)]
        internal static extern bool AXIsProcessTrustedWithOptions(IntPtr options);

        [DllImport(ApplicationServices)]
        internal static extern int AXUIElementCreateSystemWide(out IntPtr element);

        [DllImport(ApplicationServices)]
        internal static extern int AXUIElementCopyElementAtPosition(
            IntPtr application, float x, float y, out IntPtr element);

        [DllImport(ApplicationServices)]
        internal static extern int AXUIElementCopyAttributeValue(
            IntPtr element, IntPtr attribute, out IntPtr value);

        [DllImport(ApplicationServices)]
        internal static extern int AXUIElementGetPid(IntPtr element, out int pid);

        [DllImport(ApplicationServices)]
        internal static extern IntPtr AXUIElementCreateApplication(int pid);

        [DllImport(CoreFoundation, CharSet = CharSet.Ansi)]
        internal static extern IntPtr CFStringCreateWithCString(IntPtr alloc, string cStr, uint encoding);

        [DllImport(CoreFoundation)]
        internal static extern int CFStringGetLength(IntPtr theString);

        [DllImport(CoreFoundation)]
        internal static extern int CFStringGetMaximumSizeForEncoding(int length, uint encoding);

        [DllImport(CoreFoundation)]
        internal static extern bool CFStringGetCString(IntPtr theString, byte[] buffer, int bufferSize, uint encoding);

        [DllImport(CoreFoundation)]
        internal static extern IntPtr CFDictionaryCreate(IntPtr allocator, IntPtr[] keys, IntPtr[] values, int numValues, IntPtr keyCallBacks, IntPtr valueCallBacks);

        internal const uint kCFStringEncodingUTF8 = 0x08000100;

        /// <summary>把 CFStringRef 转成 C# 字符串。失败返回空串，绝不抛。</summary>
        internal static string CFStringToString(IntPtr cf)
        {
            if (cf == IntPtr.Zero) return "";
            try
            {
                int len = CFStringGetLength(cf);
                if (len <= 0) return "";
                int max = CFStringGetMaximumSizeForEncoding(len, kCFStringEncodingUTF8) + 1;
                var buf = new byte[max];
                if (!CFStringGetCString(cf, buf, max, kCFStringEncodingUTF8)) return "";
                int n = 0;
                while (n < buf.Length && buf[n] != 0) n++;
                return System.Text.Encoding.UTF8.GetString(buf, 0, n);
            }
            catch (Exception) { return ""; }
        }

        /// <summary>造一个 CFString（调用方负责 CFRelease）。</summary>
        internal static IntPtr MakeCFString(string s)
        {
            return CFStringCreateWithCString(IntPtr.Zero, s, kCFStringEncodingUTF8);
        }

        // ------------------------------------------------------------ CoreFoundation

        internal const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

        [DllImport(CoreFoundation)]
        internal static extern void CFRelease(IntPtr cf);

        // ------------------------------------------------------------ 取进程/应用名
        //
        // 「来源」在 Win32 版里是进程名（chrome.exe）。mac 上对应的东西是
        // 应用显示名（Google Chrome / 微信），取法：
        //   pid -> NSRunningApplication(applicationWithProcessId:) -> localizedName
        // 比 Process.GetProcessById().ProcessName 更贴近用户在界面上看到的名字。

        internal const string AppKit = "/System/Library/Frameworks/AppKit.framework/AppKit";

        [DllImport(AppKit)]
        internal static extern IntPtr objc_getClass(string name);

        [DllImport(AppKit)]
        internal static extern IntPtr sel_registerName(string name);

        [DllImport(AppKit)]
        internal static extern IntPtr objc_msgSend(IntPtr receiver, IntPtr selector);

        [DllImport(AppKit)]
        internal static extern IntPtr objc_msgSend(IntPtr receiver, IntPtr selector, int arg);

        /// <summary>pid → 应用显示名。取不到返回空串。</summary>
        internal static string AppNameForPid(int pid)
        {
            IntPtr cls = IntPtr.Zero, app = IntPtr.Zero, name = IntPtr.Zero;
            try
            {
                cls = objc_getClass("NSRunningApplication");
                if (cls == IntPtr.Zero) return "";

                app = objc_msgSend(cls, sel_registerName("applicationWithProcessId:"), pid);
                if (app == IntPtr.Zero) return "";

                name = objc_msgSend(app, sel_registerName("localizedName"));
                if (name == IntPtr.Zero) return "";

                // NSString -> UTF8 C 字符串。用 UTF8String 拿指针，再自己拷出来。
                var utf8 = objc_msgSend(name, sel_registerName("UTF8String"));
                if (utf8 == IntPtr.Zero) return "";
                return Marshal.PtrToStringUTF8(utf8) ?? "";
            }
            catch (Exception) { return ""; }
        }

        // ------------------------------------------------------------ 常用辅助

        [DllImport(CoreGraphics)]
        internal static extern IntPtr CGEventCreate(IntPtr source);

        /// <summary>取当前鼠标位置（点，全局显示坐标系，原点左上、y 向下）。</summary>
        internal static CGPoint GetCursorLocation()
        {
            // CGEventCreate(NULL) 返回一个当前位置的事件；用完必须 release，否则泄漏。
            var ev = CGEventCreate(IntPtr.Zero);
            if (ev == IntPtr.Zero) return new CGPoint(0, 0);
            try { return CGEventGetLocation(ev); }
            finally { CFRelease(ev); }
        }
    }
}
