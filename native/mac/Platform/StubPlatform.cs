using System;
using System.Collections.Generic;

namespace RemeberEr
{
    /// <summary>
    /// P1 阶段的平台桩：所有能力都返回「空」，但**绝不抛异常**。
    /// 目的是让窗口与数据层先跑起来，逐个把真实现换进来（P3 起换成 MacPlatform）。
    ///
    /// 每个方法都带 [!] 标记，方便在日志里一眼看出哪些还是假的。
    /// </summary>
    public class StubPlatform : IPlatform
    {
        public string Name { get { return "stub"; } }

        public IReadOnlyList<DisplayInfo> GetDisplays()
        {
            return new List<DisplayInfo>
            {
                new DisplayInfo { Id = 1, X = 0, Y = 0, W = 1920, H = 1080, Scale = 2.0, IsPrimary = true },
            };
        }

        public Screenshot CaptureDisplay(DisplayInfo d) { return null; }

        public Screenshot CaptureDisplayDimmed(DisplayInfo d, PixelRect rect) { return null; }

        public bool HasScreenCapturePermission() { return false; }

        public void RequestScreenCapturePermission() { }

        public void RegisterHotkey(string id, HotkeyModifiers mods, uint virtualKey, Action callback) { }

        public void UnregisterAllHotkeys() { }

        public ForegroundInfo ProbeForegroundWindow() { return ForegroundInfo.Empty; }

        public bool HasAccessibilityPermission() { return false; }

        public void RequestAccessibilityPermission() { }

        public PhysPoint GetCursorPosition() { return new PhysPoint(0, 0); }

        public void SetTopmost(object topLevel, bool on) { }

        public void MoveWindow(object topLevel, double physicalX, double physicalY) { }
    }
}
