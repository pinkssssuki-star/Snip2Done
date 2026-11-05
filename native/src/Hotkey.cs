using System;
using System.Text;
using System.Windows.Input;

namespace RemeberEr
{
    /// <summary>
    /// 全局热键的「字符串 ⇄ 虚拟键码」转换。
    ///
    /// 存进 data.json 的是人能读的 "Ctrl+Shift+A"，不是裸数字 ——
    /// 用户手改配置时看得懂，换了版本也不会因为常量调整而全部失效。
    /// </summary>
    public static class Hotkey
    {
        public const string DefaultCapture = "Ctrl+Shift+A";
        public const string DefaultToggle = "Ctrl+Shift+H";
        public const string DefaultQuit = "Ctrl+Shift+Q";

        // RegisterHotKey 的修饰位
        public const int MOD_ALT = 0x0001;
        public const int MOD_CONTROL = 0x0002;
        public const int MOD_SHIFT = 0x0004;
        public const int MOD_WIN = 0x0008;
        public const int MOD_NOREPEAT = 0x4000;

        const int MODS_ANY = MOD_ALT | MOD_CONTROL | MOD_SHIFT | MOD_WIN;

        /// <summary>把 "Ctrl+Shift+A" 拆成 mods + vk。失败返回 false。</summary>
        public static bool TryParse(string text, out int mods, out int vk)
        {
            mods = 0;
            vk = 0;
            if (string.IsNullOrEmpty(text)) return false;

            string[] parts = text.Split('+');
            int keyIndex = -1;

            for (int i = 0; i < parts.Length; i++)
            {
                string p = parts[i].Trim();
                if (p.Length == 0) return false;

                if (IsCtrl(p)) { mods |= MOD_CONTROL; continue; }
                if (string.Equals(p, "Shift", StringComparison.OrdinalIgnoreCase)) { mods |= MOD_SHIFT; continue; }
                if (string.Equals(p, "Alt", StringComparison.OrdinalIgnoreCase)) { mods |= MOD_ALT; continue; }
                if (IsWin(p)) { mods |= MOD_WIN; continue; }

                // 非修饰键。只允许出现在最后一段 —— "A+Ctrl" 这种写法不认
                if (keyIndex >= 0) return false;
                keyIndex = i;
            }

            if (keyIndex != parts.Length - 1) return false;
            if (!TryParseKey(parts[keyIndex].Trim(), out vk)) return false;

            // 至少一个修饰键：不然会把普通打字键抢走，等于毁掉键盘
            return (mods & MODS_ANY) != 0;
        }

        static bool IsCtrl(string p)
        {
            return string.Equals(p, "Ctrl", StringComparison.OrdinalIgnoreCase)
                || string.Equals(p, "Control", StringComparison.OrdinalIgnoreCase);
        }

        static bool IsWin(string p)
        {
            return string.Equals(p, "Win", StringComparison.OrdinalIgnoreCase)
                || string.Equals(p, "Windows", StringComparison.OrdinalIgnoreCase);
        }

        static bool TryParseKey(string p, out int vk)
        {
            vk = 0;
            if (p.Length == 0) return false;

            if (p.Length == 1)
            {
                char c = char.ToUpperInvariant(p[0]);
                if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')) { vk = c; return true; }
                return false;
            }

            // F1..F24
            if ((p[0] == 'F' || p[0] == 'f') && p.Length <= 3)
            {
                int n;
                if (int.TryParse(p.Substring(1), out n) && n >= 1 && n <= 24)
                {
                    vk = 0x6F + n;
                    return true;
                }
            }

            Key k;
            if (Enum.TryParse<Key>(p, true, out k) && k != Key.None)
            {
                int v = KeyInterop.VirtualKeyFromKey(k);
                if (v != 0) { vk = v; return true; }
            }
            return false;
        }

        /// <summary>把 mods + vk 拼回 "Ctrl+Shift+A"。顺序固定，方便比较和显示。</summary>
        public static string Format(int mods, int vk)
        {
            var sb = new StringBuilder();
            if ((mods & MOD_CONTROL) != 0) sb.Append("Ctrl+");
            if ((mods & MOD_ALT) != 0) sb.Append("Alt+");
            if ((mods & MOD_SHIFT) != 0) sb.Append("Shift+");
            if ((mods & MOD_WIN) != 0) sb.Append("Win+");
            sb.Append(KeyName(vk));
            return sb.ToString();
        }

        /// <summary>虚拟键码 → 短名字。数字和字母给字符本身（不是 Key.D1 那种内部名）。</summary>
        public static string KeyName(int vk)
        {
            if (vk >= 0x30 && vk <= 0x39) return ((char)vk).ToString();      // 0-9
            if (vk >= 0x41 && vk <= 0x5A) return ((char)vk).ToString();      // A-Z
            if (vk >= 0x70 && vk <= 0x87) return "F" + (vk - 0x6F);          // F1-F24
            if (vk >= 0x60 && vk <= 0x69) return "Num" + (vk - 0x60);        // 小键盘 0-9
            Key k = KeyInterop.KeyFromVirtualKey(vk);
            return k == Key.None ? ("VK" + vk) : k.ToString();
        }

        /// <summary>这个 key 是不是纯修饰键（左/右 Ctrl、Shift、Alt、Win）—— 按下它不该算「选好了」。</summary>
        public static bool IsModifierKey(Key k)
        {
            return k == Key.LeftCtrl || k == Key.RightCtrl
                || k == Key.LeftShift || k == Key.RightShift
                || k == Key.LeftAlt || k == Key.RightAlt
                || k == Key.LWin || k == Key.RWin
                || k == Key.System;
        }

        /// <summary>把一次按键（含当时按住的修饰键）变成 "Ctrl+Shift+A"。返回 null 表示这次按键还不能当组合键。</summary>
        public static string FromKeyEvent(Key key, ModifierKeys mods)
        {
            if (IsModifierKey(key)) return null;

            int m = 0;
            if ((mods & ModifierKeys.Control) != 0) m |= MOD_CONTROL;
            if ((mods & ModifierKeys.Alt) != 0) m |= MOD_ALT;
            if ((mods & ModifierKeys.Shift) != 0) m |= MOD_SHIFT;
            if ((mods & ModifierKeys.Windows) != 0) m |= MOD_WIN;
            if (m == 0) return null;   // 和 TryParse 同一条规矩：必须带修饰键

            int vk = KeyInterop.VirtualKeyFromKey(key);
            if (vk == 0) return null;
            return Format(m, vk);
        }
    }
}
