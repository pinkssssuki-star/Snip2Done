using System;
using System.Diagnostics;
using Microsoft.Win32;

namespace RemeberEr
{
    /// <summary>
    /// 开机自启。
    ///
    /// 走 HKCU\...\CurrentVersion\Run 而不是「启动」文件夹的快捷方式 ——
    /// 一句话就写完了，不用造 .lnk，也不用管快捷方式被谁改过。
    /// HKCU 是当前用户，**不需要管理员权限**，这点对免安装的 exe 很重要。
    ///
    /// 判断「开着没」不看自己记的标志，而是**读注册表里实际写的路径**：
    /// 用户可能自己删了、或者把 exe 挪了位置。路径对不上就当没开 ——
    /// 不然开关会显示「已开启」而实际上开不了机。
    /// </summary>
    public static class AutoStart
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string ValueName = "RemeberEr";

        /// <summary>当前 exe 的完整路径。带引号写注册表，防路径里有空格。</summary>
        public static string ExePath
        {
            get
            {
                try
                {
                    var p = Process.GetCurrentProcess().MainModule.FileName;
                    if (!string.IsNullOrEmpty(p)) return p;
                }
                catch (Exception) { }
                return System.Reflection.Assembly.GetEntryAssembly().Location;
            }
        }

        public static bool IsEnabled
        {
            get
            {
                try
                {
                    using (var k = Registry.CurrentUser.OpenSubKey(RunKey, false))
                    {
                        if (k == null) return false;
                        var v = k.GetValue(ValueName) as string;
                        if (string.IsNullOrEmpty(v)) return false;
                        return v.IndexOf(ExePath, StringComparison.OrdinalIgnoreCase) >= 0;
                    }
                }
                catch (Exception) { return false; }
            }
        }

        /// <summary>开或关。返回是否操作成功 —— 组策略锁了 HKCU 的话会失败。</summary>
        public static bool Set(bool on)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (k == null) return false;
                    if (on) k.SetValue(ValueName, "\"" + ExePath + "\"");
                    else k.DeleteValue(ValueName, false);
                    return true;
                }
            }
            catch (Exception ex)
            {
                Program.Log("  开机自启设置失败：" + ex.Message);
                return false;
            }
        }
    }
}
