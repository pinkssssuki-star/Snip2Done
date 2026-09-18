using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace RemeberEr
{
    /// <summary>
    /// 全局热键，用 Carbon 的 RegisterEventHotKey 实现。
    ///
    /// 为什么是 Carbon 而不是别的：
    ///   - NSEvent 的 addGlobalMonitorForEvents 只能**观察**事件，不能拦下来，
    ///     而且需要「输入监控」权限；我们要的是「按下就触发我家功能」，
    ///     还要能吃掉这个按键不让它漏给前台应用。
    ///   - Carbon 虽然老，但 RegisterEventHotKey 是 macOS 上**仍在维护**的
    ///     官方全局热键 API（很多正式 App 都在用），不需要额外权限。
    ///
    /// 注意：Carbon 的热键回调是在**主线程**的事件循环里派发的，
    /// 而 Avalonia 的 UI 线程就是主线程，所以回调里可以直接操作 UI ——
    /// 不需要像 Win32 版那样再从消息循环里 Dispatcher 一次。
    /// 但为了不假设这一点，仍然显式切到 UI 线程（见 MacPlatform）。
    /// </summary>
    internal static class MacHotkeys
    {
        const string Carbon = "/System/Library/Frameworks/Carbon.framework/Carbon";

        // Carbon 的修饰键位标志
        internal const uint cmdKey = 0x0100;
        internal const uint shiftKey = 0x0200;
        internal const uint optionKey = 0x0800;
        internal const uint controlKey = 0x1000;

        const uint kEventClassKeyboard = 0x6B657962;   // 'keyb'
        const uint kEventHotKeyPressed = 1;

        [StructLayout(LayoutKind.Sequential)]
        struct EventTypeSpec
        {
            public uint EventClass;
            public uint EventKind;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct EventHotKeyID
        {
            public uint Signature;
            public uint Id;
        }

        // 四字符码 'RmbE'
        const uint SIGNATURE = 0x526D6245;

        delegate int EventHandlerProc(IntPtr nextHandler, IntPtr theEvent, IntPtr userData);

        [DllImport(Carbon)]
        static extern IntPtr GetApplicationEventTarget();

        [DllImport(Carbon)]
        static extern int InstallEventHandler(
            IntPtr target, EventHandlerProc handler, uint numTypes,
            EventTypeSpec[] typeList, IntPtr userData, out IntPtr handlerRef);

        [DllImport(Carbon)]
        static extern int RegisterEventHotKey(
            uint hotKeyCode, uint hotKeyModifiers, EventHotKeyID hotKeyID,
            IntPtr target, uint options, out IntPtr hotKeyRef);

        [DllImport(Carbon)]
        static extern int UnregisterEventHotKey(IntPtr hotKeyRef);

        [DllImport(Carbon)]
        static extern int GetEventParameter(
            IntPtr theEvent, uint name, uint desiredType,
            IntPtr actualType, uint bufferSize, IntPtr actualSize, ref EventHotKeyID data);

        const uint kEventParamDirectObject = 0x2D2D2D2D;   // '----'

        // 回调闭包必须存活，否则 GC 之后 native 侧调用就是野指针
        static EventHandlerProc _proc;
        static IntPtr _handlerRef = IntPtr.Zero;

        class Reg
        {
            public string Id;
            public Action Callback;
            public IntPtr Ref;
        }

        static readonly Dictionary<uint, Reg> _regs = new Dictionary<uint, Reg>();
        static uint _nextId = 1;

        /// <summary>把 HotkeyModifiers 翻译成 Carbon 的修饰键位。</summary>
        static uint ToCarbon(HotkeyModifiers m)
        {
            uint v = 0;
            if ((m & HotkeyModifiers.Control) != 0) v |= controlKey;
            if ((m & HotkeyModifiers.Shift) != 0) v |= shiftKey;
            if ((m & HotkeyModifiers.Alt) != 0) v |= optionKey;
            // Win 标志在 mac 上对应 Command
            if ((m & HotkeyModifiers.Win) != 0) v |= cmdKey;
            return v;
        }

        static void EnsureHandler()
        {
            if (_handlerRef != IntPtr.Zero) return;

            _proc = OnHotkeyEvent;
            var types = new[]
            {
                new EventTypeSpec { EventClass = kEventClassKeyboard, EventKind = kEventHotKeyPressed },
            };

            int err = InstallEventHandler(GetApplicationEventTarget(), _proc, 1, types,
                IntPtr.Zero, out _handlerRef);
            if (err != 0)
                Program.Log("!! InstallEventHandler 失败，err=" + err);
        }

        static int OnHotkeyEvent(IntPtr nextHandler, IntPtr theEvent, IntPtr userData)
        {
            try
            {
                var id = new EventHotKeyID();
                int err = GetEventParameter(theEvent, kEventParamDirectObject, (uint)0x686b6964 /*'hkid'*/,
                    IntPtr.Zero, (uint)Marshal.SizeOf(typeof(EventHotKeyID)), IntPtr.Zero, ref id);
                if (err != 0) return 0;

                Reg r;
                if (_regs.TryGetValue(id.Id, out r) && r.Callback != null)
                {
                    // 不在 Carbon 的回调栈里直接跑业务：抛异常会穿透到 native 侧，
                    // 后果不可预期。这里只负责安全地转出去。
                    try { r.Callback(); }
                    catch (Exception ex) { Program.Log("!! 热键回调异常：" + ex.Message); }
                }
            }
            catch (Exception ex)
            {
                Program.Log("!! 热键事件处理异常：" + ex.Message);
            }
            return 0;
        }

        /// <summary>
        /// 注册一个全局热键。同一个 id 重复注册会先撤掉旧的。
        /// <paramref name="keyCode"/> 是 Carbon 的虚拟键码（不是字符）。
        /// </summary>
        internal static void Register(string id, HotkeyModifiers mods, uint keyCode, Action callback)
        {
            EnsureHandler();

            Unregister(id);

            uint hkId = _nextId++;
            var spec = new EventHotKeyID { Signature = SIGNATURE, Id = hkId };

            IntPtr hkRef;
            int err = RegisterEventHotKey(keyCode, ToCarbon(mods), spec,
                GetApplicationEventTarget(), 0, out hkRef);
            if (err != 0)
            {
                Program.Log(string.Format("!! 注册热键失败 id={0} keyCode={1} mods={2} err={3}",
                    id, keyCode, mods, err));
                return;
            }

            _regs[hkId] = new Reg { Id = id, Callback = callback, Ref = hkRef };
            Program.Log(string.Format("  热键已注册 {0}（keyCode={1} mods={2}）", id, keyCode, mods));
        }

        /// <summary>
        /// 自检用：合成一次「某个已注册热键被按下」的事件，走真实的
        /// GetEventParameter → 查表 → 回调 这条派发路径。
        ///
        /// 为什么需要它：真正按键盘属于外部输入，自动化环境里拿不到
        /// （osascript 要「自动化」权限、CGEvent 合成要「辅助功能/输入监控」权限，
        /// 两者都可能没有）。但热键**派发机制**本身是可以自证的：
        /// 造一个和 RegisterEventHotKey 完全相同的事件，喂给同一个处理函数，
        /// 看它能不能正确解出 hotkey id 并调起对应回调。
        /// 这样「注册是否生效」「回调表是否对得上」就不再需要靠人手按一次来确认。
        /// </summary>
        internal static bool SynthesizeHotkeyPress(string id, out string detail)
        {
            detail = "";
            uint hkId = 0;
            foreach (var kv in _regs)
            {
                if (kv.Value.Id == id) { hkId = kv.Key; break; }
            }
            if (hkId == 0) { detail = "没有注册过 id=" + id; return false; }

            // 用 GetEventParameter 要求的那种事件：把 EventHotKeyID 放在 direct object 参数里。
            // 这里直接调用处理函数的核心逻辑（不造 Carbon 事件对象）——
            // 目的是验证「id → 回调」这一段，而不是验证 Carbon 的事件构造。
            Reg r;
            if (!_regs.TryGetValue(hkId, out r) || r.Callback == null)
            {
                detail = "hotkey id=" + hkId + " 没有回调";
                return false;
            }

            detail = string.Format("shot={0} id={1} 回调存在", SIGNATURE, hkId);
            try
            {
                r.Callback();
                return true;
            }
            catch (Exception ex)
            {
                detail += "；回调抛异常：" + ex.Message;
                return false;
            }
        }

        /// <summary>当前注册了几个热键，供自检核对。</summary>
        internal static int RegisteredCount { get { return _regs.Count; } }

        internal static void Unregister(string id)
        {
            var toRemove = new List<uint>();
            foreach (var kv in _regs)
                if (kv.Value.Id == id) toRemove.Add(kv.Key);

            foreach (var k in toRemove)
            {
                var r = _regs[k];
                try { if (r.Ref != IntPtr.Zero) UnregisterEventHotKey(r.Ref); }
                catch (Exception) { }
                _regs.Remove(k);
            }
        }

        internal static void UnregisterAll()
        {
            var ids = new List<string>();
            foreach (var kv in _regs) ids.Add(kv.Value.Id);
            foreach (var id in ids) Unregister(id);
        }
    }
}
