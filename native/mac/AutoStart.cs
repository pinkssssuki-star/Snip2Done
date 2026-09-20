using System;
using System.Diagnostics;
using System.IO;

namespace RemeberEr
{
    /// <summary>
    /// 开机自启（macOS 实现）。
    ///
    /// 对应 Windows 版的 HKCU\...\CurrentVersion\Run —— 但 mac 上没有「注册表 Run 键」
    /// 这个等价物，正规做法是往 <c>~/Library/LaunchAgents/</c> 放一份 launchd plist。
    /// 两者语义相同：都是「当前用户登录后自动起一个进程」，都不需要管理员权限。
    ///
    /// 与原版一致的一条原则：判断「开着没」不去看自己记的标志，而是**读实际写下的内容**。
    /// 用户可能自己删了那个 plist、或者把 .app 挪了位置。路径对不上就当没开 ——
    /// 不然开关会显示「已开启」而实际上开不了机。
    ///
    /// 关于 .app 与裸可执行文件的区别（这一条会影响写进 plist 的是什么）：
    /// 如果是从 .app 启动的，写 `open -a <app>`；否则直接写可执行文件路径。
    /// 前者让 launchd 起的是正常的应用（有 bundle 身份，权限也挂在它身上）。
    /// </summary>
    public static class AutoStart
    {
        const string Label = "com.remeberer.mac";

        static string PlistPath
        {
            get
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (string.IsNullOrEmpty(home)) home = Environment.GetEnvironmentVariable("HOME");
                return Path.Combine(home, "Library", "LaunchAgents", Label + ".plist");
            }
        }

        /// <summary>
        /// 当前应该自启的目标。优先用 .app 包（正常应用身份），
        /// 退回到可执行文件路径。
        ///
        /// 判定「在不在 .app 里」靠上溯目录：可执行文件位于
        /// Xxx.app/Contents/MacOS/ 时，往上两层就是 .app。
        /// </summary>
        public static string Target
        {
            get
            {
                try
                {
                    string exe = Process.GetCurrentProcess().MainModule.FileName;
                    var macOs = Path.GetDirectoryName(exe);            // .../Contents/MacOS
                    var contents = Path.GetDirectoryName(macOs);       // .../Contents
                    if (contents != null && Path.GetFileName(contents) == "Contents")
                    {
                        var app = Path.GetDirectoryName(contents);     // Xxx.app
                        if (!string.IsNullOrEmpty(app) && app.EndsWith(".app", StringComparison.Ordinal))
                            return app;
                    }
                    return exe;
                }
                catch (Exception) { return null; }
            }
        }

        /// <summary>
        /// 上游叫 ExePath。mac 上没有单一的 exe —— 可能是 .app 包，也可能是裸可执行文件，
        /// 所以这里返回的是「自启要指向的那个目标」，语义与上游一致（都是"要起的东西"）。
        /// </summary>
        public static string ExePath { get { return Target; } }

        static bool IsAppBundle
        {
            get { var t = Target; return t != null && t.EndsWith(".app", StringComparison.Ordinal); }
        }

        /// <summary>现在是不是开着。以 launchd plist 的实际内容为准。</summary>
        public static bool IsEnabled
        {
            get
            {
                try
                {
                    if (!File.Exists(PlistPath)) return false;
                    string txt = File.ReadAllText(PlistPath);
                    string target = Target;
                    if (string.IsNullOrEmpty(target)) return false;

                    // 路径必须对得上：plist 里写的还是老位置就当没开
                    // （用户把 .app 挪走之后，那个 plist 起不来东西）
                    return txt.Contains(target);
                }
                catch (Exception) { return false; }
            }
        }

        /// <summary>打开自启。成功返回 true。</summary>
        public static bool Enable()
        {
            try
            {
                string target = Target;
                if (string.IsNullOrEmpty(target)) return false;

                Directory.CreateDirectory(Path.GetDirectoryName(PlistPath));

                // 用 .app 时走 `open -a`：这样 launchd 起的是一个正常应用，
                // 而不是一个没有 bundle 身份的裸进程（后者会让系统权限对不上号）。
                string programArgs;
                if (IsAppBundle)
                {
                    programArgs =
                        "    <key>ProgramArguments</key>\n" +
                        "    <array>\n" +
                        "        <string>/usr/bin/open</string>\n" +
                        "        <string>-a</string>\n" +
                        "        <string>" + Escape(target) + "</string>\n" +
                        "    </array>\n";
                }
                else
                {
                    programArgs =
                        "    <key>ProgramArguments</key>\n" +
                        "    <array>\n" +
                        "        <string>" + Escape(target) + "</string>\n" +
                        "    </array>\n";
                }

                string plist =
                    "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
                    "<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" " +
                    "\"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n" +
                    "<plist version=\"1.0\">\n" +
                    "<dict>\n" +
                    "    <key>Label</key>\n" +
                    "    <string>" + Label + "</string>\n" +
                    programArgs +
                    "    <key>RunAtLoad</key>\n" +
                    "    <true/>\n" +
                    // 常驻工具：被系统回收后希望它自己回来
                    "    <key>KeepAlive</key>\n" +
                    "    <false/>\n" +
                    "</dict>\n" +
                    "</plist>\n";

                File.WriteAllText(PlistPath, plist, new System.Text.UTF8Encoding(false));

                // 尽量让 launchd 立刻载入；失败不算失败 —— 下次登录照样会生效。
                // 这一步只是「现在就开始生效」，不是自启本身的前提。
                RunLaunchctl("unload", PlistPath);
                RunLaunchctl("load", PlistPath);

                Program.Log("  开机自启已开启 -> " + PlistPath);
                Program.Log("    目标 = " + target + (IsAppBundle ? "（.app 包）" : "（裸可执行文件）"));
                return IsEnabled;
            }
            catch (Exception ex)
            {
                Program.Log("  !! 开启自启失败：" + ex.GetType().Name + " " + ex.Message);
                return false;
            }
        }

        /// <summary>关掉自启（删掉 plist 并 unload）。</summary>
        public static bool Disable()
        {
            try
            {
                if (File.Exists(PlistPath))
                {
                    RunLaunchctl("unload", PlistPath);
                    File.Delete(PlistPath);
                    Program.Log("  开机自启已关闭（已删除 " + PlistPath + "）");
                }
                return !IsEnabled;
            }
            catch (Exception ex)
            {
                Program.Log("  !! 关闭自启失败：" + ex.GetType().Name + " " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 开 / 关自启，返回操作后「是否真的开着」。
        /// 注意返回的是**实际状态**而不是「操作有没有报错」——
        /// 与上游一致：调用方拿它去刷新开关，不能出现「显示已开启但其实没写进去」。
        /// </summary>
        public static bool Set(bool on)
        {
            return on ? Enable() : Disable();
        }

        static void RunLaunchctl(string verb, string path)
        {
            try
            {
                var psi = new ProcessStartInfo("/bin/launchctl")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                };
                psi.ArgumentList.Add(verb);
                psi.ArgumentList.Add(path);
                using (var p = Process.Start(psi))
                {
                    p.WaitForExit(3000);
                }
            }
            catch (Exception) { /* launchctl 不可用时忽略：plist 文件本身已经写好了 */ }
        }

        static string Escape(string s)
        {
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        }
    }
}
