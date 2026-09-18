using System;
using System.Collections.Generic;

namespace RemeberEr
{
    /// <summary>一块屏幕。坐标一律是**物理像素**，和 Win32 版的口径一致。</summary>
    public class DisplayInfo
    {
        /// <summary>系统给的显示器编号（CGDirectDisplayID）。</summary>
        public uint Id;
        /// <summary>物理像素矩形（虚拟屏坐标系）。</summary>
        public double X, Y, W, H;
        /// <summary>缩放系数。Retina 屏通常是 2.0。</summary>
        public double Scale;
        /// <summary>是不是主屏。</summary>
        public bool IsPrimary;

        public override string ToString()
        {
            return string.Format("Display#{0} ({1},{2}) {3}x{4} scale={5}{6}",
                Id, X, Y, W, H, Scale, IsPrimary ? " [primary]" : "");
        }
    }

    /// <summary>
    /// 一张位图。像素格式统一 **BGRA8888 预乘无关（straight）**，
    /// 和 Win32 版 BitBlt 出来再转出来的口径对齐，避免两边颜色不一致。
    /// </summary>
    public class Screenshot
    {
        public int Width;
        public int Height;
        /// <summary>BGRA，每行 Width*4 字节，无 padding。</summary>
        public byte[] Bgra;

        public int Stride { get { return Width * 4; } }
    }

    /// <summary>「用户此刻在看哪个窗口」的结果。</summary>
    public class ForegroundInfo
    {
        /// <summary>应用名（对应 Win32 版的进程名，如 chrome.exe → Google Chrome）。</summary>
        public string Source = "";
        /// <summary>窗口标题。</summary>
        public string Title = "";

        public static readonly ForegroundInfo Empty = new ForegroundInfo();
    }

    /// <summary>修饰键。和 Win32 版的 MOD_* 位标志保持同样的语义。</summary>
    [Flags]
    public enum HotkeyModifiers
    {
        None = 0,
        Alt = 1,
        Control = 2,
        Shift = 4,
        Win = 8,      // mac 上对应 Command
    }

    /// <summary>
    /// 平台能力接口 —— 取代 WPF 版的 Interop.cs。
    ///
    /// Windows 版把 34 处 Win32 P/Invoke 散在 Interop.cs 里由各窗口直接调；
    /// 移植时收成这一个接口，两个平台各给一份实现，好处是：
    ///   1. 上层窗口代码不再出现任何平台专有调用，6 个窗口的移植量因此小很多；
    ///   2. 换抓屏后端（CG → ScreenCaptureKit）只动实现，不动调用方。
    /// </summary>
    public interface IPlatform
    {
        /// <summary>当前接入的平台名，用于日志（"macOS" / "Windows"）。</summary>
        string Name { get; }

        /// <summary>枚举所有显示器，物理像素坐标。</summary>
        IReadOnlyList<DisplayInfo> GetDisplays();

        /// <summary>抓一整块屏。未授权「屏幕录制」时返回 null（绝不能静默返回黑图）。</summary>
        Screenshot CaptureDisplay(DisplayInfo d);

        /// <summary>
        /// 抓一整块屏，同时把物理像素矩形 <paramref name="rect"/> 之外的区域涂黑。
        /// 框选层用它给每块屏铺一张「暗掉的底图」。
        /// </summary>
        Screenshot CaptureDisplayDimmed(DisplayInfo d, PixelRect rect);

        /// <summary>屏幕录制权限是否已授予。未授予时上层要引导用户去系统设置。</summary>
        bool HasScreenCapturePermission();

        /// <summary>请求屏幕录制权限（会弹系统引导）。</summary>
        void RequestScreenCapturePermission();

        /// <summary>
        /// 注册全局热键。同一个 id 重复注册视为覆盖。
        /// 回调在**后台线程**触发，上层负责切回 UI 线程。
        /// </summary>
        void RegisterHotkey(string id, HotkeyModifiers mods, uint virtualKey, Action callback);

        /// <summary>注销全部热键（退出时调）。</summary>
        void UnregisterAllHotkeys();

        /// <summary>
        /// 探测「鼠标底下那个窗口」的应用名与标题。
        /// 必须在自己的浮层窗口出现**之前**调用 —— 否则只会探到自己。
        /// 没有「辅助功能」权限时降级为返回应用名，取不到就返回 Empty，绝不抛异常。
        /// </summary>
        ForegroundInfo ProbeForegroundWindow();

        /// <summary>辅助功能权限是否已授予。</summary>
        bool HasAccessibilityPermission();

        /// <summary>请求辅助功能权限（会弹系统引导）。</summary>
        void RequestAccessibilityPermission();

        /// <summary>鼠标当前位置，物理像素。</summary>
        PhysPoint GetCursorPosition();

        /// <summary>把窗口设为始终置顶 / 取消。</summary>
        void SetTopmost(object topLevel, bool on);

        /// <summary>把窗口移到指定物理像素位置（不改变大小）。</summary>
        void MoveWindow(object topLevel, double physicalX, double physicalY);
    }

    /// <summary>
    /// 物理像素坐标点。
    /// 刻意不叫 PixelPoint —— Avalonia 自己有一个同名类型，重名会让调用处
    /// 需要到处写全限定名，很容易看错（踩过一次编译错误）。
    /// </summary>
    public struct PhysPoint
    {
        public double X, Y;
        public PhysPoint(double x, double y) { X = x; Y = y; }
        public override string ToString() { return string.Format("({0},{1})", X, Y); }
    }

    /// <summary>物理像素矩形。用 double 是为了和缩放运算少一次取整。</summary>
    public struct PixelRect
    {
        public double X, Y, W, H;
        public PixelRect(double x, double y, double w, double h) { X = x; Y = y; W = w; H = h; }
        public bool IsEmpty { get { return W <= 0 || H <= 0; } }
    }
}
