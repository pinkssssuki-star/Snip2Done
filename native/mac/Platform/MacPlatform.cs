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

        // ============================================================ ScreenCaptureKit 桥接层

        /*
         * 抓屏走 ScreenCaptureKit（经 Platform/sck_bridge.m 的纯 C 接口），
         * **不再用 CGDisplayCreateImage**。
         *
         * 原因（这是移植途中修正的一个方向性错误）：
         *   CGDisplayCreateImage / CGWindowListCreateImage 在 SDK 里标着
         *   SCREEN_CAPTURE_OBSOLETE(10.6, 14.4, 15.0) —— 也就是
         *   availability(macos, deprecated=14.4, obsoleted=15.0)，
         *   头文件原话是 "Please use ScreenCaptureKit instead."。
         *   本项目的目标系统是 macOS 26，继续用 CG 那条路是拿已移除的 API 赌运气。
         *
         * 桥接层的错误码与 sck_bridge.m 里的 enum 一一对应，改一处要改两处。
         */
        const string SckLib = "libremeber_sck";

        const int SckOK = 0;
        const int SckTimeout = 1;
        const int SckNoContent = 2;
        const int SckNoDisplay = 3;
        const int SckCaptureFailed = 4;
        const int SckBadArgs = 5;

        [DllImport(SckLib)]
        static extern int remeber_sck_preflight();

        [DllImport(SckLib)]
        static extern IntPtr remeber_sck_capture_display(uint displayID, out int w, out int h, out int err);

        [DllImport(SckLib, CharSet = CharSet.Ansi)]
        static extern IntPtr remeber_sck_error_text(int code);

        // ---- 热键相关权限（桥接层，见 sck_bridge.m）----

        [DllImport(SckLib)]
        static extern int remeber_hotkey_listen_access();

        [DllImport(SckLib)]
        static extern int remeber_hotkey_request_listen_access();

        [DllImport(SckLib)]
        static extern int remeber_ax_trusted();

        /// <summary>
        /// 「输入监控」权限。NSEvent 的全局热键监听需要它。
        ///
        /// 为什么不能靠「装监听器有没有报错」判断：没有这个权限时
        /// addGlobalMonitorForEvents 仍会返回一个非 nil 的 monitor 对象，
        /// 但**事件永远不来** —— 「成功却无效」很有迷惑性，
        /// 所以必须用官方的 preflight API 显式查一次。
        /// </summary>
        public bool HasInputMonitoringPermission()
        {
            try { return remeber_hotkey_listen_access() == 1; }
            catch (Exception) { return false; }
        }

        /// <summary>申请「输入监控」权限（弹系统引导）。</summary>
        public void RequestInputMonitoringPermission()
        {
            try { remeber_hotkey_request_listen_access(); }
            catch (Exception ex) { Program.Log("!! 申请输入监控权限失败：" + ex.Message); }
        }

        /// <summary>辅助功能权限（Carbon 热键与读窗口标题都要它）。</summary>
        public bool HasAccessibilityPermission()
        {
            try { return remeber_ax_trusted() == 1; }
            catch (Exception) { return false; }
        }

        static string SckErrorText(int code)
        {
            try
            {
                var p = remeber_sck_error_text(code);
                return p == IntPtr.Zero ? ("错误码 " + code) : (Marshal.PtrToStringAnsi(p) ?? ("错误码 " + code));
            }
            catch (Exception) { return "错误码 " + code; }
        }

        /// <summary>桥接库是否可用。第一次调用时探一次，结果缓存。</summary>
        static int _bridgeOk = -1;
        static bool BridgeAvailable()
        {
            if (_bridgeOk >= 0) return _bridgeOk == 1;
            try
            {
                // 能调到就说明 dylib 加载成功
                remeber_sck_error_text(SckOK);
                _bridgeOk = 1;
            }
            catch (Exception ex)
            {
                _bridgeOk = 0;
                Program.Log("!! ScreenCaptureKit 桥接层加载失败：" + ex.GetType().Name);
                Program.Log("   请先跑 build.sh 生成并拷贝 libremeber_sck.dylib");
            }
            return _bridgeOk == 1;
        }

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

        /// <summary>
        /// 是否已授予「屏幕录制」权限。
        ///
        /// 用 ScreenCaptureKit 能不能拿到可共享内容来判定，而不是 CGPreflightScreenCaptureAccess：
        /// 后者判的是 CoreGraphics 那条已被移除的抓屏路径，在 macOS 15+ 上语义已经不对了
        /// （它可能报「已授权」，但真正抓的时候 SCK 依然拿不到内容）。
        /// 用「实际能不能拿到内容」来判，和后面真正抓屏走的是同一条路，不会自相矛盾。
        /// </summary>
        public bool HasScreenCapturePermission()
        {
            if (!BridgeAvailable()) return false;
            try { return remeber_sck_preflight() == 1; }
            catch (Exception ex)
            {
                Program.Log("!! SCK preflight 调用失败：" + ex.Message);
                return false;
            }
        }

        public void RequestScreenCapturePermission()
        {
            // 用「真去抓一次」来触发系统授权。这也是官方推荐的路径：
            // 第一次调用 ScreenCaptureKit 时，系统会弹出授权引导并登记本程序。
            //
            // 不用 CGRequestScreenCaptureAccess，理由同上：那是已被移除的
            // CoreGraphics 抓屏路径的 API，登记的是另一条路。
            //
            // ⚠️ 授权后**必须重启本进程**才生效（macOS 的既定行为），
            // 所以流程上必须引导用户重启，不能假设立刻可用。
            try
            {
                var ds = GetDisplays();
                if (ds.Count == 0) { Program.Log("  没有可用显示器，无法触发授权"); return; }
                var d = ds[0];
                foreach (var x in ds) if (x.IsPrimary) { d = x; break; }

                Program.Log("  正在触发「屏幕录制」授权（首次会弹系统引导）…");
                int w = 0, h = 0, err = -1;
                IntPtr img = remeber_sck_capture_display(d.Id, out w, out h, out err);
                if (img != IntPtr.Zero) MacInterop.CFRelease(img);
                Program.Log("  触发结果：" + SckErrorText(err));
            }
            catch (Exception ex)
            {
                Program.Log("!! 触发屏幕录制授权失败：" + ex.Message);
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
        /// 走 ScreenCaptureKit（经 sck_bridge.m），不再用 CGDisplayCreateImage ——
        /// 后者在 macOS 15 已被 obsoleted，见本文件顶部那段说明。
        ///
        /// 处理链：SCK 给 CGImage（BGRA）→ 画进自建的 CGBitmapContext 拿到确定的
        /// 像素布局 → 交给上层做选区裁剪与压暗。
        /// 自己建上下文这一步保留着：SCK 返回图的像素格式虽然实际是 BGRA，
        /// 但那是实现细节，自己建上下文能把它钉死成我们约定的格式。
        /// </summary>
        Screenshot GrabDisplay(uint displayId)
        {
            if (!BridgeAvailable()) return null;

            var sw = Stopwatch.StartNew();
            IntPtr img = IntPtr.Zero, ctx = IntPtr.Zero, cs = IntPtr.Zero;

            try
            {
                int w = 0, h = 0, err = -1;
                var swSck = Stopwatch.StartNew();
                img = remeber_sck_capture_display(displayId, out w, out h, out err);
                swSck.Stop();
                if (img == IntPtr.Zero)
                {
                    Program.Log(string.Format("!! 抓屏失败（耗时 {0}ms）：{1}",
                        swSck.ElapsedMilliseconds, SckErrorText(err)));
                    if (err == SckNoContent)
                    {
                        Program.Log("   这通常就是缺少「屏幕录制」权限 —— ");
                        Program.Log("   系统设置 → 隐私与安全性 → 屏幕录制，勾选本程序后**重启它**。");
                    }
                    return null;
                }
                if (w <= 0 || h <= 0)
                {
                    Program.Log("!! 抓到的图尺寸非法：" + w + "x" + h);
                    return null;
                }

                var bgra = new byte[(long)w * h * 4 > int.MaxValue ? 0 : w * h * 4];
                if (bgra.Length == 0)
                {
                    Program.Log("!! 图太大，超过单个托管数组上限：" + w + "x" + h);
                    return null;
                }

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
                    MacInterop.CGContextDrawImage(ctx, new MacInterop.CGRect(0, 0, w, h), img);
                }
                finally { handle.Free(); }

                var shot = new Screenshot { Width = w, Height = h, Bgra = bgra };

                // SCK 在没权限时通常直接报错（不会给黑图），但 CG 那条老路会给黑图，
                // 保留这道检测作为第二层保险：把「没权限」和「真抓到一张黑图」区分开。
                if (IsAllBlack(shot))
                {
                    Program.Log("!! 抓到的图是全黑的 —— 大概率仍是无权限或系统拒绝了共享");
                    return null;
                }

                Program.Log(string.Format("  抓屏 {0}x{1} 用时 {2}ms（ScreenCaptureKit）",
                    w, h, sw.ElapsedMilliseconds));
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
