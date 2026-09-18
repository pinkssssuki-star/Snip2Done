using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace RemeberEr
{
    /// <summary>原生版要用到的 Win32 —— 全在 C# 里，不再走 PowerShell。</summary>
    public static class Interop
    {
        public const int SM_XVIRTUALSCREEN = 76;
        public const int SM_YVIRTUALSCREEN = 77;
        public const int SM_CXVIRTUALSCREEN = 78;
        public const int SM_CYVIRTUALSCREEN = 79;
        public const int SRCCOPY = 0x00CC0020;
        public const int CAPTUREBLT = 0x40000000;

        public const int WM_HOTKEY = 0x0312;
        public const int WM_NCLBUTTONDOWN = 0x00A1;
        public const int HTCAPTION = 2;
        public const int MOD_CONTROL = 0x0002;
        public const int MOD_SHIFT = 0x0004;
        public const int MOD_NOREPEAT = 0x4000;

        public const int GA_ROOT = 2;

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X, Y; }

        [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
        [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
        [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr o);
        [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);

        [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr h, int id, int mods, int vk);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr h, int id);
        [DllImport("user32.dll")] public static extern bool ReleaseCapture();
        [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
        [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint flags);
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder sb, int max);

        // 注意：user32 并不导出 GetWindowLongPtr / SetWindowLongPtr（那是宏），
        // 而且扩展样式只占 32 位，所以直接用 W 版的 32 位函数最省事。
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] public static extern int GetWindowLong(IntPtr h, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] public static extern int SetWindowLong(IntPtr h, int index, int val);
        public const int GWL_EXSTYLE = -20;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_NOACTIVATE = 0x08000000;

        [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);
        [DllImport("dwmapi.dll")] public static extern int DwmExtendFrameIntoClientArea(IntPtr h, ref MARGINS m);
        public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        public const int DWMWCP_DONOTROUND = 1;
        public const int DWMWCP_ROUND = 2;

        public const int WM_NCHITTEST = 0x0084;
        public const int HTCLIENT = 1;

        [StructLayout(LayoutKind.Sequential)]
        public struct MARGINS { public int Left, Right, Top, Bottom; }

        /// <summary>
        /// 让无边框的不透明窗口拿到 DWM 的圆角和投影。
        /// 分层窗口（AllowsTransparency=True）在 150% 副屏上销毁时会分段重绘，所以圆形弹窗一律走这条。
        /// </summary>
        public static void EnableDwmChrome(IntPtr hwnd)
        {
            try
            {
                // 扩一点边框，DWM 才会给无边框窗口画投影
                var m = new MARGINS { Left = 1, Right = 1, Top = 1, Bottom = 1 };
                DwmExtendFrameIntoClientArea(hwnd, ref m);
                int corner = DWMWCP_ROUND;
                DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, 4);
            }
            catch (Exception) { }
        }

        // ---- 显示器 / DPI / 精确摆窗（框选层要正好盖住一整块屏幕）----

        [StructLayout(LayoutKind.Sequential)]
        public struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public int dwFlags;
        }

        [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(POINT pt, int flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFO mi);
        [DllImport("shcore.dll")] public static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);
        [DllImport("shcore.dll")] public static extern int GetProcessDpiAwareness(IntPtr hProcess, out int awareness);

        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);

        // ---- 合成鼠标输入（只给自动化验收用，验证真实拖拽路径）----

        [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] public static extern void mouse_event(uint flags, int dx, int dy, uint data, IntPtr extra);
        public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        public const uint MOUSEEVENTF_LEFTUP = 0x0004;

        public static void MouseDown() { mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, IntPtr.Zero); }
        public static void MouseUp() { mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, IntPtr.Zero); }

        /// <summary>找一块缩放不是 100% 的显示器中心，用来复现混 DPI 下的坐标问题。</summary>
        public static bool FindScaledMonitorCenter(out int cx, out int cy, out double scale)
        {
            cx = cy = 0; scale = 1;
            int vx = GetSystemMetrics(SM_XVIRTUALSCREEN), vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
            int vw = GetSystemMetrics(SM_CXVIRTUALSCREEN), vh = GetSystemMetrics(SM_CYVIRTUALSCREEN);

            // 在虚拟屏上撒点探测，第一块缩放 > 1.05 的屏就是我们要的
            for (int gy = 0; gy <= 4; gy++)
            {
                for (int gx = 0; gx <= 4; gx++)
                {
                    int px = vx + vw * gx / 4 + vw / 8;
                    int py = vy + vh * gy / 4 + vh / 8;
                    IntPtr mon = MonitorFromPoint(new POINT { X = px, Y = py }, MONITOR_DEFAULTTONEAREST);
                    if (mon == IntPtr.Zero) continue;
                    var mi = new MONITORINFO();
                    mi.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
                    if (!GetMonitorInfoW(mon, ref mi)) continue;
                    uint dx = 0, dy = 0;
                    try { if (GetDpiForMonitor(mon, MDT_EFFECTIVE_DPI, out dx, out dy) != 0) continue; }
                    catch (Exception) { continue; }
                    if (dx / 96.0 <= 1.05) continue;

                    cx = (mi.rcMonitor.Left + mi.rcMonitor.Right) / 2;
                    cy = (mi.rcMonitor.Top + mi.rcMonitor.Bottom) / 2;
                    scale = dx / 96.0;
                    return true;
                }
            }
            return false;
        }

        public const int MONITOR_DEFAULTTONEAREST = 2;
        public const int MDT_EFFECTIVE_DPI = 0;
        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        public static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
        public const uint SWP_NOACTIVATE = 0x0010;
        public const uint SWP_SHOWWINDOW = 0x0040;
        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOZORDER = 0x0004;

        [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hwnd);

        /// <summary>窗口所在那块屏的 DPI 缩放。</summary>
        public static double ScaleOfWindow(IntPtr hwnd)
        {
            try
            {
                uint dpi = GetDpiForWindow(hwnd);
                if (dpi > 0) return dpi / 96.0;
            }
            catch (Exception) { }
            return 1.0;
        }

        /// <summary>某个点所在那块屏的工作区（物理像素，已扣掉任务栏）。</summary>
        public static bool WorkAreaAt(int x, int y, out RECT work)
        {
            work = new RECT();
            IntPtr mon = MonitorFromPoint(new POINT { X = x, Y = y }, MONITOR_DEFAULTTONEAREST);
            if (mon == IntPtr.Zero) return false;
            var mi = new MONITORINFO();
            mi.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
            if (!GetMonitorInfoW(mon, ref mi)) return false;
            work = mi.rcWork;
            return true;
        }

        /// <summary>
        /// 鼠标所在的那块屏：物理矩形 + 它的 DPI 缩放。
        /// 混 DPI 双屏下必须按屏算，不能拿主屏的缩放去套。
        /// </summary>
        public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT lprcMonitor, IntPtr dwData);
        [DllImport("user32.dll")]
        public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

        /// <summary>所有显示器的物理矩形（顺序不保证）。</summary>
        public static List<RECT> AllMonitors()
        {
            var list = new List<RECT>();
            try
            {
                MonitorEnumProc cb = (IntPtr h, IntPtr hdc, ref RECT r, IntPtr d) =>
                {
                    list.Add(new RECT { Left = r.Left, Top = r.Top, Right = r.Right, Bottom = r.Bottom });
                    return true;
                };
                EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, cb, IntPtr.Zero);
                GC.KeepAlive(cb);
            }
            catch (Exception) { }
            return list;
        }

        public static bool SameRect(RECT a, RECT b)
        {
            return a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;
        }

        /// <summary>某块屏的 DPI 缩放。</summary>
        public static double ScaleOf(RECT mon)
        {
            double s = 1.0;
            try
            {
                var pt = new POINT { X = (mon.Left + mon.Right) / 2, Y = (mon.Top + mon.Bottom) / 2 };
                IntPtr h = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
                uint dx = 0, dy = 0;
                if (GetDpiForMonitor(h, MDT_EFFECTIVE_DPI, out dx, out dy) == 0 && dx > 0) s = dx / 96.0;
            }
            catch (Exception) { }
            return s;
        }

        public static bool MonitorUnderCursor(out RECT rect, out double scale)
        {            rect = new RECT();
            scale = 1.0;
            POINT p;
            if (!GetCursorPos(out p)) return false;

            IntPtr mon = MonitorFromPoint(p, MONITOR_DEFAULTTONEAREST);
            if (mon == IntPtr.Zero) return false;

            var mi = new MONITORINFO();
            mi.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
            if (!GetMonitorInfoW(mon, ref mi)) return false;
            rect = mi.rcMonitor;

            try
            {
                uint dx, dy;
                if (GetDpiForMonitor(mon, MDT_EFFECTIVE_DPI, out dx, out dy) == 0 && dx > 0)
                    scale = dx / 96.0;
            }
            catch (Exception)
            {
                // shcore.dll 不在（很老的系统）就退回 1:1
            }
            return true;
        }

        /// <summary>当前前台窗口；如果前台是我们自己，就用鼠标底下的窗口。</summary>
        public static IntPtr ForegroundTarget(IntPtr selfHwnd)
        {
            IntPtr h = GetForegroundWindow();
            if (h == selfHwnd || h == IntPtr.Zero)
            {
                POINT p;
                GetCursorPos(out p);
                h = GetAncestor(WindowFromPoint(p), GA_ROOT);
            }
            return h;
        }

        public static string TitleOf(IntPtr h)
        {
            var sb = new StringBuilder(512);
            GetWindowTextW(h, sb, sb.Capacity);
            return sb.ToString().Trim();
        }

        /// <summary>窗口属于哪个进程 —— 也就是「来源软件」，如 msedge / Code / Weixin。</summary>
        public static string ProcessNameOf(IntPtr h)
        {
            if (h == IntPtr.Zero) return "";
            uint pid;
            GetWindowThreadProcessId(h, out pid);
            if (pid == 0) return "";
            try
            {
                using (var p = System.Diagnostics.Process.GetProcessById((int)pid))
                    return p.ProcessName;
            }
            catch (Exception) { return ""; }
        }

        /// <summary>截图时记下「来源软件 + 窗口标题」，这是纯图片原则下唯一保留的文本元数据。</summary>
        public static void ForegroundInfo(IntPtr selfHwnd, out string app, out string title)
        {
            IntPtr h = ForegroundTarget(selfHwnd);
            app = ProcessNameOf(h);
            title = TitleOf(h);
            if (string.IsNullOrEmpty(title) && string.IsNullOrEmpty(app)) title = "（未识别窗口）";
        }

        /// <summary>把窗口排除在 Alt+Tab 之外（预览窗、以及不想出现在任务栏的辅助窗）。</summary>
        public static void HideFromAltTab(IntPtr hwnd)
        {
            int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW);
        }
    }
}
