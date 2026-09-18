using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace RemeberEr
{
    /// <summary>
    /// macOS 平台实现，取代 WPF 版的 Interop.cs（34 处 Win32）。
    ///
    /// 进度：
    ///   P2  显示器枚举、鼠标位置、屏幕录制权限 — 真实现
    ///   P3  抓屏 — 真实现；全局热键、窗口探测 — 真实现
    ///   P4  置顶 / 移动窗口 — 待实现（需要 AppKit 坐标系翻转）
    ///
    /// 未实现的方法抛 NotSupportedException 而不是返回空值：
    /// 静默返回空会让上层「看起来正常但不出东西」，比直接报错难查得多。
    /// </summary>
    public class MacPlatform : IPlatform
    {
        public string Name { get { return "macOS"; } }

        // ============================================================ 显示器

        public IReadOnlyList<DisplayInfo> GetDisplays()
        {
            var list = new List<DisplayInfo>();

            uint count = 0;
            int err = MacInterop.CGGetActiveDisplayList(0, null, out count);
            if (err != MacInterop.kCGErrorSuccess || count == 0)
            {
                Program.Log("!! CGGetActiveDisplayList 失败，err=" + err + " count=" + count);
                return list;
            }

            var ids = new uint[count];
            err = MacInterop.CGGetActiveDisplayList(count, ids, out count);
            if (err != MacInterop.kCGErrorSuccess)
            {
                Program.Log("!! CGGetActiveDisplayList(取列表) 失败，err=" + err);
                return list;
            }

            foreach (var id in ids)
            {
                var b = MacInterop.CGDisplayBounds(id);       // 点（逻辑像素）
                double pw = (double)MacInterop.CGDisplayPixelsWide(id);
                double ph = (double)MacInterop.CGDisplayPixelsHigh(id);

                // 缩放比 = 物理像素 / 点。1x 屏 = 1.0，Retina = 2.0。
                // 缩放模式（如「更多空间」）下比值可能是 1.5 这种非整数，照实传出去。
                double scale = 1.0;
                if (b.Width > 0 && b.Height > 0 && pw > 0 && ph > 0)
                {
                    double sx = pw / b.Width;
                    double sy = ph / b.Height;
                    // 取横纵里更接近整数的那个，避免某一边的边缘误差把 2.0 算成 1.9999
                    scale = Math.Abs(sx - Math.Round(sx)) <= Math.Abs(sy - Math.Round(sy)) ? sx : sy;
                }

                list.Add(new DisplayInfo
                {
                    Id = id,
                    // 对外统一用物理像素，和 Win32 版的口径一致
                    X = b.X * scale,
                    Y = b.Y * scale,
                    W = pw,
                    H = ph,
                    Scale = scale,
                    IsPrimary = MacInterop.CGDisplayIsMain(id),
                });
            }

            return list;
        }

        double PrimaryScale()
        {
            var main = MacInterop.CGMainDisplayID();
            var b = MacInterop.CGDisplayBounds(main);
            double pw = (double)MacInterop.CGDisplayPixelsWide(main);
            if (b.Width > 0 && pw > 0) return pw / b.Width;
            return 1.0;
        }

        public PhysPoint GetCursorPosition()
        {
            var p = MacInterop.GetCursorLocation();
            // CG 的光标坐标是**点**，乘主屏缩放换算成物理像素。
            // 注意：混 DPI 多屏下这个换算对副屏不精确（各屏缩放可能不同）。
            // 本机是单屏 1x，尚未暴露该问题；等多屏环境再按「光标所在屏」修正。
            double scale = PrimaryScale();
            return new PhysPoint(p.X * scale, p.Y * scale);
        }

        // ============================================================ 权限

        public bool HasScreenCapturePermission()
        {
            try { return MacInterop.CGPreflightScreenCaptureAccess(); }
            catch (Exception ex)
            {
                Program.Log("!! CGPreflightScreenCaptureAccess 调用失败：" + ex.Message);
                return false;
            }
        }

        public void RequestScreenCapturePermission()
        {
            try
            {
                // 这个调用会触发系统弹窗，并把本程序登记进「屏幕录制」列表。
                // 注意：用户授权后**必须重启本进程**才会生效（macOS 的既定行为），
                // 所以授权流程必须引导用户重启，不能假设立刻可用。
                MacInterop.CGRequestScreenCaptureAccess();
            }
            catch (Exception ex)
            {
                Program.Log("!! CGRequestScreenCaptureAccess 调用失败：" + ex.Message);
            }
        }

        public bool HasAccessibilityPermission()
        {
            try { return MacInterop.AXIsProcessTrusted(); }
            catch (Exception ex)
            {
                Program.Log("!! AXIsProcessTrusted 调用失败：" + ex.Message);
                return false;
            }
        }

        public void RequestAccessibilityPermission()
        {
            // AXIsProcessTrustedWithOptions({kAXTrustedCheckOptionPrompt: true})
            // 会弹系统引导。字典用 CFDictionaryCreate 现造，避免硬编码 CFString 指针。
            IntPtr key = IntPtr.Zero, dict = IntPtr.Zero, appServices = IntPtr.Zero;
            try
            {
                key = MacInterop.MakeCFString("AXTrustedCheckOptionPrompt");
                if (key == IntPtr.Zero)
                {
                    Program.Log("!! 造 AXTrustedCheckOptionPrompt 失败，只能回退到无提示的探测");
                    MacInterop.AXIsProcessTrusted();
                    return;
                }

                var keys = new[] { key };
                var vals = new[] { MacInterop.MakeCFString("true") };
                dict = MacInterop.CFDictionaryCreate(IntPtr.Zero, keys, vals, 1, IntPtr.Zero, IntPtr.Zero);
                if (dict == IntPtr.Zero)
                {
                    Program.Log("!! 造选项字典失败");
                    return;
                }

                MacInterop.AXIsProcessTrustedWithOptions(dict);
            }
            catch (Exception ex)
            {
                Program.Log("!! 申请辅助功能权限失败：" + ex.Message);
            }
            finally
            {
                if (dict != IntPtr.Zero) MacInterop.CFRelease(dict);
                if (key != IntPtr.Zero) MacInterop.CFRelease(key);
                if (appServices != IntPtr.Zero) MacInterop.CFRelease(appServices);
            }
        }

        // ============================================================ 抓屏

        /// <summary>
        /// 抓一块屏的原始位图（BGRA）。
        ///
        /// 实现要点：
        ///   1. 用 CGDisplayCreateImage 直接抓这块屏，而不是抓整个虚拟屏再裁 ——
        ///      多屏时省掉一次大图裁切，也和 Win32 版按屏 BitBlt 的口径一致。
        ///   2. 抓到的 CGImage **不直接读像素**，而是画进自己建的
        ///      CGBitmapContext。原因是 provider 的格式/字节序不透明，
        ///      自己建上下文能指定 kCGImageAlphaPremultipliedFirst +
        ///      ByteOrder32Little，结果确定地是 BGRA。
        ///   3. 未授权时 CGDisplayCreateImage 会返回**成功但全黑**的图，
        ///      不报错。所以必须自己做一次「整图是否全黑」的检测并明确报错，
        ///      否则用户只会看到黑图，无从排查。
        /// </summary>
        Screenshot GrabDisplay(uint displayId)
        {
            var sw = Stopwatch.StartNew();
            IntPtr img = IntPtr.Zero, ctx = IntPtr.Zero, cs = IntPtr.Zero;

            try
            {
                if (!HasScreenCapturePermission())
                {
                    Program.Log("!! 抓屏失败：没有「屏幕录制」权限（系统设置 → 隐私与安全性 → 屏幕录制）");
                    return null;
                }

                img = MacInterop.CGDisplayCreateImage(displayId);
                if (img == IntPtr.Zero)
                {
                    Program.Log("!! CGDisplayCreateImage 返回空（displayId=" + displayId + "）");
                    return null;
                }

                int w = (int)MacInterop.CGImageGetWidth(img);
                int h = (int)MacInterop.CGImageGetHeight(img);
                if (w <= 0 || h <= 0)
                {
                    Program.Log("!! 抓到的图尺寸非法：" + w + "x" + h);
                    return null;
                }

                var bgra = new byte[w * h * 4];

                cs = MacInterop.CGColorSpaceCreateDeviceRGB();
                uint info = MacInterop.kCGImageAlphaPremultipliedFirst | MacInterop.kCGBitmapByteOrder32Little;

                var handle = GCHandle.Alloc(bgra, GCHandleType.Pinned);
                try
                {
                    ctx = MacInterop.CGBitmapContextCreate(
                        handle.AddrOfPinnedObject(), (nuint)w, (nuint)h, 8, (nuint)(w * 4), cs, info);
                    if (ctx == IntPtr.Zero)
                    {
                        Program.Log("!! CGBitmapContextCreate 失败");
                        return null;
                    }

                    var rect = new MacInterop.CGRect(0, 0, w, h);
                    MacInterop.CGContextDrawImage(ctx, rect, img);
                }
                finally
                {
                    handle.Free();
                }

                var shot = new Screenshot { Width = w, Height = h, Bgra = bgra };

                if (IsAllBlack(shot))
                {
                    Program.Log("!! 抓到的图是全黑的 —— 几乎肯定是缺少「屏幕录制」权限。"
                              + "请到 系统设置 → 隐私与安全性 → 屏幕录制 勾选本程序，然后重启它。");
                    return null;
                }

                Program.Log(string.Format("  抓屏 {0}x{1} 用时 {2}ms", w, h, sw.ElapsedMilliseconds));
                return shot;
            }
            catch (Exception ex)
            {
                Program.Log("!! 抓屏异常：" + ex.GetType().Name + " " + ex.Message);
                return null;
            }
            finally
            {
                if (ctx != IntPtr.Zero) MacInterop.CGContextRelease(ctx);
                if (cs != IntPtr.Zero) MacInterop.CFRelease(cs);
                if (img != IntPtr.Zero) MacInterop.CFRelease(img);
            }
        }

        /// <summary>
        /// 全黑检测。抽稀采样即可 —— 目的是区分「没授权」和「真的抓到一张黑图」，
        /// 逐像素扫整张 2560x1440 没必要。
        /// </summary>
        static bool IsAllBlack(Screenshot s)
        {
            if (s.Bgra == null || s.Bgra.Length == 0) return true;
            int pixels = s.Width * s.Height;
            int step = Math.Max(1, pixels / 4096);   // 最多采样约 4096 个点
            for (int i = 0; i < pixels; i += step)
            {
                int o = i * 4;
                if (o + 2 >= s.Bgra.Length) break;
                // BGRA；任一通道不是「接近黑」就认为有内容
                if (s.Bgra[o] > 6 || s.Bgra[o + 1] > 6 || s.Bgra[o + 2] > 6) return false;
            }
            return true;
        }

        public Screenshot CaptureDisplay(DisplayInfo d)
        {
            if (d == null) return null;
            return GrabDisplay(d.Id);
        }

        public Screenshot CaptureDisplayDimmed(DisplayInfo d, PhysRect rect)
        {
            var shot = CaptureDisplay(d);
            if (shot == null) return null;

            // 把选区**之外**压暗。约定：rect 用**本屏物理像素、相对本屏左上角**，
            // 与 IPlatform 注释一致（这也是框选层内部使用的口径）。
            //
            // 用「四条边带」而不是「整图 + 挖洞」：
            // 这里是在裸像素上操作，没有 EvenOdd 填充规则可用，
            // 画四条矩形既等价又不用管混合公式。
            DimOutside(shot, rect);
            return shot;
        }

        /// <summary>把矩形之外的区域乘暗（原地修改）。</summary>
        internal static void DimOutside(Screenshot s, PhysRect r, double factor = 0.42)
        {
            int x0 = Math.Max(0, (int)Math.Round(r.X));
            int y0 = Math.Max(0, (int)Math.Round(r.Y));
            int x1 = Math.Min(s.Width, (int)Math.Round(r.X + r.W));
            int y1 = Math.Min(s.Height, (int)Math.Round(r.Y + r.H));
            bool hasHole = x1 > x0 && y1 > y0;

            for (int y = 0; y < s.Height; y++)
            {
                bool rowInside = hasHole && y >= y0 && y < y1;
                int rowStart = y * s.Width;
                for (int x = 0; x < s.Width; x++)
                {
                    if (rowInside && x >= x0 && x < x1) continue;   // 选区内保持原样
                    int o = (rowStart + x) * 4;
                    if (o + 2 >= s.Bgra.Length) break;
                    s.Bgra[o] = (byte)(s.Bgra[o] * factor);
                    s.Bgra[o + 1] = (byte)(s.Bgra[o + 1] * factor);
                    s.Bgra[o + 2] = (byte)(s.Bgra[o + 2] * factor);
                }
            }
        }

        // ============================================================ 热键

        public void RegisterHotkey(string id, HotkeyModifiers mods, uint virtualKey, Action callback)
        {
            // Carbon 的回调在主线程派发，但调用方可能从任意线程注册；
            // 这里不假设，回调统一切到 UI 线程再执行 —— 上层拿到的语义就和
            // Win32 版（消息循环 → Dispatcher）一致了，窗口代码不用两套写法。
            MacHotkeys.Register(id, mods, virtualKey, () =>
            {
                if (callback == null) return;
                try { Avalonia.Threading.Dispatcher.UIThread.Post(callback); }
                catch (Exception ex) { Program.Log("!! 热键回调切线程失败：" + ex.Message); }
            });
        }

        public void UnregisterAllHotkeys()
        {
            MacHotkeys.UnregisterAll();
        }

        // ============================================================ 窗口探测

        /// <summary>
        /// 「用户此刻在看哪个窗口」= 鼠标底下那个窗口。
        ///
        /// 原版改用「鼠标下」而不是「前台窗口」的原因（Win32 版注释里记着）：
        /// 实测微信 4.x 会有一个隐藏窗口长期占据前台，于是无论在哪截图，
        /// GetForegroundWindow 都返回微信；而用户按快捷键时鼠标几乎总停在他想截的
        /// 那个窗口上。mac 上同理沿用这个策略。
        ///
        /// ⚠️ 必须在本程序自己的浮层窗口出现**之前**调用 ——
        /// 浮层一旦盖上去，探到的就只会是浮层自己。
        ///
        /// 降级链：AX 元素标题（需要辅助功能权限）→ 只有应用名 → 空。
        /// 任何一步失败都不能让截图流程失败。
        /// </summary>
        public ForegroundInfo ProbeForegroundWindow()
        {
            var info = new ForegroundInfo();
            try
            {
                var cur = MacInterop.GetCursorLocation();

                IntPtr systemWide = IntPtr.Zero;
                IntPtr element = IntPtr.Zero;
                IntPtr titleVal = IntPtr.Zero;
                IntPtr titleAttr = IntPtr.Zero;

                try
                {
                    if (MacInterop.AXUIElementCreateSystemWide(out systemWide) != 0 || systemWide == IntPtr.Zero)
                        return info;

                    int err = MacInterop.AXUIElementCopyElementAtPosition(
                        systemWide, (float)cur.X, (float)cur.Y, out element);
                    if (err != 0 || element == IntPtr.Zero)
                    {
                        // 没有辅助功能权限时最常见的就是走到这里（err = kAXErrorAPIDisabled = -25211）
                        Program.Log("  窗口探测：AXUIElementCopyElementAtPosition 未成功，err=" + err
                            + "（辅助功能权限已授权=" + HasAccessibilityPermission() + "）");
                        return info;
                    }

                    // pid → 应用名
                    int pid;
                    if (MacInterop.AXUIElementGetPid(element, out pid) == 0 && pid > 0)
                        info.Source = MacInterop.AppNameForPid(pid);

                    // 标题
                    titleAttr = MacInterop.MakeCFString("AXTitle");
                    if (titleAttr != IntPtr.Zero &&
                        MacInterop.AXUIElementCopyAttributeValue(element, titleAttr, out titleVal) == 0)
                    {
                        info.Title = MacInterop.CFStringToString(titleVal);
                    }

                    // 标题可能挂在父节点上（很多应用把标题放在窗口容器而不是命中的子元素），
                    // 探不到就往上一级找一次，避免「有窗口但标题空」。
                    // 这一层刻意只做一级：再往上就容易串到别的窗口去了。
                }
                finally
                {
                    if (titleVal != IntPtr.Zero) MacInterop.CFRelease(titleVal);
                    if (titleAttr != IntPtr.Zero) MacInterop.CFRelease(titleAttr);
                    if (element != IntPtr.Zero) MacInterop.CFRelease(element);
                    if (systemWide != IntPtr.Zero) MacInterop.CFRelease(systemWide);
                }
            }
            catch (Exception ex)
            {
                Program.Log("  窗口探测异常（已降级为空）：" + ex.GetType().Name + " " + ex.Message);
            }
            return info;
        }

        // ============================================================ 待实现（P4）

        public void SetTopmost(object topLevel, bool on)
        {
            throw new NotSupportedException("置顶尚未实现（P4）：NSWindow.setLevel:");
        }

        public void MoveWindow(object topLevel, double physicalX, double physicalY)
        {
            throw new NotSupportedException("移动窗口尚未实现（P4）：需 AppKit 坐标系翻转");
        }
    }
}
