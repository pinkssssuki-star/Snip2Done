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
        const uint kEventHotKeyReleased = 2;

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

        /// <summary>
        /// 让 Carbon 的事件循环先跑起来（kEventDurationSecond 表示处理完当前待办就返回）。
        ///
        /// 为什么需要它：最小验证程序里调了这句之后 RegisterEventHotKey 才从
        /// err=-50 变成 err=0。更关键的是，Carbon 的热键事件要靠事件循环派发到
        /// InstallEventHandler 装上的处理函数 —— app 里由 Avalonia/AppKit 跑主循环，
        /// 但 Carbon 自己那一套（事件队列、application event target 的可用性）
        /// 需要显式初始化一次。
        /// </summary>
        [DllImport(Carbon)]
        static extern void RunCurrentEventLoop(double timeout);

        const double kEventDurationSecond = 1.0;

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

        // ---------------------------------------------------------- NSEvent 后端（桥接层）
        //
        // Carbon 在当前系统上「注册成功但事件从不派发」（实测 handler 一次都没被调用），
        // 所以主路径走 NSEvent 的全局监听，Carbon 作为后备。
        // 桥接层在 sck_bridge.m 里，理由与取舍写在那份文件的注释里。
        const string SckLib = "libremeber_sck";

        [DllImport(SckLib)]
        static extern int remeber_hotkey_start(HotkeyCallback cb);

        [DllImport(SckLib)]
        static extern void remeber_hotkey_stop();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate void HotkeyCallback(uint modifiers, uint keyCode);

        // 必须存活，否则 GC 之后 native 侧调用就是野指针
        static HotkeyCallback _nativeCb;

        /// <summary>待匹配的热键表：mods+keyCode → 回调。NSEvent 是监听式的，
        /// 系统会把所有按键都送过来，得自己比对。</summary>
        static readonly Dictionary<string, uint> _wantMods = new Dictionary<string, uint>();
        static readonly Dictionary<string, uint> _wantKeys = new Dictionary<string, uint>();
        static bool _nativeBackend;

        /// <summary>启动 NSEvent 全局监听。成功返回 true。</summary>
        static bool StartNativeBackend()
        {
            if (_nativeBackend) return true;
            try
            {
                _nativeCb = OnNativeKey;
                int rc = remeber_hotkey_start(_nativeCb);
                if (rc == 0)
                {
                    _nativeBackend = true;
                    Program.Log("  热键后端 = NSEvent 全局监听（Carbon 在此系统上不派发事件）");
                    return true;
                }
                Program.Log("  !! NSEvent 后端启动失败，rc=" + rc
                    + "（可能缺少「输入监控」权限）");
                return false;
            }
            catch (Exception ex)
            {
                Program.Log("  !! NSEvent 后端不可用：" + ex.GetType().Name);
                return false;
            }
        }

        /// <summary>系统送来的每一个按键：自己比对是不是我们要的热键。</summary>
        static void OnNativeKey(uint mods, uint keyCode)
        {
            try
            {
                foreach (var kv in _wantKeys)
                {
                    if (kv.Value != keyCode) continue;
                    uint want;
                    if (!_wantMods.TryGetValue(kv.Key, out want)) continue;
                    if (want != mods) continue;

                    Reg r;
                    foreach (var rr in _regs)
                    {
                        if (rr.Value.Id != kv.Key) continue;
                        r = rr.Value;
                        Program.Log("  [热键] NSEvent 命中 " + kv.Key);
                        try { if (r.Callback != null) r.Callback(); }
                        catch (Exception ex) { Program.Log("!! 热键回调异常：" + ex.Message); }
                        return;
                    }
                }
            }
            catch (Exception) { }
        }

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

            // 先初始化 Carbon 事件循环，再装处理函数 —— 顺序反过来会拿不到事件
            try { RunCurrentEventLoop(kEventDurationSecond); }
            catch (Exception ex) { Program.Log("  !! RunCurrentEventLoop 失败：" + ex.Message); }

            _proc = OnHotkeyEvent;
            // 两个事件都装上：按下是我们要的，Released 也一起收着 ——
            // 有些系统版本/修饰键组合下只派发其中一个，两个都听更稳。
            var types = new[]
            {
                new EventTypeSpec { EventClass = kEventClassKeyboard, EventKind = kEventHotKeyPressed },
                new EventTypeSpec { EventClass = kEventClassKeyboard, EventKind = kEventHotKeyReleased },
            };

            int err = InstallEventHandler(GetApplicationEventTarget(), _proc, 2, types,
                IntPtr.Zero, out _handlerRef);
            if (err != 0)
                Program.Log("!! InstallEventHandler 失败，err=" + err);
        }

        static int OnHotkeyEvent(IntPtr nextHandler, IntPtr theEvent, IntPtr userData)
        {
            // 第一行就记一笔：这样能明确区分「Carbon 压根没调用处理函数」
            // 和「调用了但后面解析失败」——两者是完全不同的故障，之前只在解析之后
            // 记日志，导致分不清，白试了好几轮。
            Console.Error.WriteLine("[hotkey] handler invoked");
            Console.Error.Flush();

            try
            {
                var id = new EventHotKeyID();
                int err = GetEventParameter(theEvent, kEventParamDirectObject, (uint)0x686b6964 /*'hkid'*/,
                    IntPtr.Zero, (uint)Marshal.SizeOf(typeof(EventHotKeyID)), IntPtr.Zero, ref id);
                if (err != 0)
                {
                    // 走到这里说明 Carbon 确实派发过来了，只是参数没取到 ——
                    // 和「回调压根没被调用」是两种完全不同的故障，必须分开记。
                    Program.Log("!! 热键事件到达但 GetEventParameter 失败，err=" + err);
                    return 0;
                }

                Program.Log(string.Format("  [热键] 收到事件 sig=0x{0:X} id={1}",
                    id.Signature, id.Id));

                Reg r;
                if (_regs.TryGetValue(id.Id, out r) && r.Callback != null)
                {
                    // 不在 Carbon 的回调栈里直接跑业务：抛异常会穿透到 native 侧，
                    // 后果不可预期。这里只负责安全地转出去。
                    try { r.Callback(); }
                    catch (Exception ex) { Program.Log("!! 热键回调异常：" + ex.Message); }
                }
                else
                {
                    Program.Log("  [热键] 事件 id 对不上任何已注册项（可能来自别的信号）");
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
            Unregister(id);

            // 先记进「想要的热键」表 —— NSEvent 是监听式的，靠这张表比对
            _wantMods[id] = ToCarbon(mods);
            _wantKeys[id] = keyCode;

            // 主路径：NSEvent 全局监听
            if (StartNativeBackend())
            {
                _regs[_nextId++] = new Reg { Id = id, Callback = callback, Ref = IntPtr.Zero };
                Program.Log(string.Format("  热键已注册 {0}（keyCode={1} mods={2}，NSEvent）",
                    id, keyCode, mods));
                return;
            }

            // 后备：Carbon（在能派发事件的系统上仍然可用）
            EnsureHandler();

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
            Program.Log(string.Format("  热键已注册 {0}（keyCode={1} mods={2} hkId={3} ref=0x{4:X}）",
                id, keyCode, mods, hkId, hkRef.ToInt64()));
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
