using System;
using System.IO;
using System.Text;
using System.Windows;

namespace RemeberEr
{
    /// <summary>
    /// 纯原生 WPF 版的入口。
    /// 刻意自己写 Main（而不是让 MSBuild 从 App.xaml 生成），好在创建任何窗口之前
    /// 先把进程设成 Per-Monitor-V2 DPI 感知 —— 这一步晚了窗口会被系统拉伸变糊。
    /// </summary>
    public static class Program
    {
        /// <summary>发布版本号。pack.ps1 会从这里读，别在两处各写一份。</summary>
        public const string Version = "0.9.0";

        public static string Root;
        public static string DataDir;
        public static bool Bench;
        public static bool NoCapture;
        public static bool NoThumb;
        public static int BenchDelayMs = 1500;
        public static bool PrimaryOnly;
        public static string ShotPath;
        public static string DumpSnap;
        public static string StartView;

        // 自动化验收流程：截图 → 弹归类窗 → 自动选一个 → 回面板截图，全程不用手点
        // 自动化验收流程（--flow=...）：
        //   demo       截图 → 归类窗（自动选 Soon）→ 落库 → 截面板
        //   detail     打开一条 Wantdo 的详情窗 → 截图
        //   recordview 打开一条已完成条目的记录窗（回看模式）→ 截图
        //   recordwrite 打开一条 Wantdo 的记录窗（写心得模式）→ 截图
        public static string Flow;
        public static string AutoChoose;
        public static string ChooserShot;
        // 框选层的自动化验收：自动画一个 WxH（物理像素）的框然后选掉
        public static int AutoRegionW;
        public static int AutoRegionH;
        public static string OverlayShot;
        /// <summary>合成鼠标拖拽做端到端验收（覆盖真实拖拽路径）</summary>
        public static bool SynthDrag;
        public static bool DpiDiag;
        /// <summary>截图时窗口四周留多少像素（给说明文档用的图要留 0，免得带进桌面）</summary>
        public static int ShotPad = 80;
        /// <summary>验收用：强制打开快速模式（不等用户去设置里勾）</summary>
        public static bool ForceQuick;
        public static string LogPath;
        /// <summary>启动分段计时用。从 Main 第一行开始算。</summary>
        public static readonly System.Diagnostics.Stopwatch Boot = System.Diagnostics.Stopwatch.StartNew();

        [STAThread]
        public static void Main(string[] args)
        {
            bool dpiOk = false;
            try { dpiOk = Interop.SetProcessDpiAwarenessContext(new IntPtr(-4)); } // PER_MONITOR_AWARE_V2
            catch (Exception) { }

            foreach (var a in args)
            {
                if (a.StartsWith("--root=")) Root = a.Substring(7);
                else if (a.StartsWith("--data=")) DataDir = a.Substring(7);
                else if (a == "--bench") Bench = true;
                else if (a == "--nocapture") NoCapture = true;
                else if (a == "--nothumb") NoThumb = true;
                else if (a.StartsWith("--benchdelay=")) BenchDelayMs = int.Parse(a.Substring(13));
                else if (a == "--primary") PrimaryOnly = true;
                else if (a.StartsWith("--shot=")) ShotPath = a.Substring(7);
                else if (a.StartsWith("--dumpsnap=")) DumpSnap = a.Substring(11);
                else if (a.StartsWith("--now=")) Store.NowOverride = long.Parse(a.Substring(6));
                else if (a.StartsWith("--view=")) StartView = a.Substring(7);
                else if (a.StartsWith("--flow="))
                {
                    Flow = a.Substring(7);
                    if (Flow == "demo")
                    {
                        AutoChoose = "soon";
                        // 没人手动拖框，给一个默认选区
                        if (AutoRegionW == 0) { AutoRegionW = 900; AutoRegionH = 600; }
                    }
                }
                else if (a.StartsWith("--autoregion="))
                {
                    var parts = a.Substring(13).Split('x');
                    if (parts.Length == 2)
                    {
                        AutoRegionW = int.Parse(parts[0]);
                        AutoRegionH = int.Parse(parts[1]);
                    }
                }
                else if (a.StartsWith("--overlayshot=")) OverlayShot = a.Substring(14);
                else if (a == "--synthdrag") { SynthDrag = true; Flow = "synthdrag"; }
                else if (a == "--dpidiag") DpiDiag = true;
                else if (a.StartsWith("--shotpad=")) ShotPad = int.Parse(a.Substring(10));
                else if (a == "--quick") ForceQuick = true;
                else if (a.StartsWith("--autochoose=")) AutoChoose = a.Substring(13);
                else if (a.StartsWith("--choosershot=")) ChooserShot = a.Substring(14);
            }

            if (Root == null) Root = AppDomain.CurrentDomain.BaseDirectory;

            // 数据默认放在 %APPDATA%\remeber-er —— 和 Electron 版同一个目录，
            // 所以装了旧版的人打开原生版就能直接看到自己已有的条目。
            // 刻意不写在 exe 旁边：发给朋友的包多半解压在「下载」里，
            // 在那里拉出一个数据目录既脏，重装/换位置还会丢。
            if (DataDir == null)
            {
                DataDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "remeber-er");
            }
            try { Directory.CreateDirectory(DataDir); } catch (Exception) { }
            LogPath = Path.Combine(DataDir, "native.log");
            try { File.Delete(LogPath); } catch (Exception) { }   // 每次启动重开一份，别越滚越大

            // 崩了也要留下痕迹 —— WinExe 没有控制台，不然只能看到一个 0xE0434352
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                Log("!! 未处理异常：" + e.ExceptionObject);

            Log("记忆面板 v" + Version + (Bench ? "（基准测试）" : ""));
            Log("  程序目录 = " + Root);
            Log("  数据目录 = " + DataDir);

            // 诊断：DPI 感知没设上的话，混 DPI 双屏下框选层会被系统缩放，图和坐标都对不齐
            {
                var pt = new Interop.POINT { X = 0, Y = 0 };
                var mon = Interop.MonitorFromPoint(pt, Interop.MONITOR_DEFAULTTONEAREST);
                var mi = new Interop.MONITORINFO();
                mi.cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(Interop.MONITORINFO));
                Interop.GetMonitorInfoW(mon, ref mi);
                uint dx = 0, dy = 0;
                try { Interop.GetDpiForMonitor(mon, Interop.MDT_EFFECTIVE_DPI, out dx, out dy); } catch (Exception) { }
                Log(string.Format("  DPI: 感知设置={0} 主屏=({1},{2}) {3}x{4} dpi={5}",
                    dpiOk, mi.rcMonitor.Left, mi.rcMonitor.Top,
                    mi.rcMonitor.Right - mi.rcMonitor.Left, mi.rcMonitor.Bottom - mi.rcMonitor.Top, dx));
                Log(string.Format("  虚拟屏: 原点=({0},{1}) 尺寸={2}x{3}",
                    Interop.GetSystemMetrics(Interop.SM_XVIRTUALSCREEN),
                    Interop.GetSystemMetrics(Interop.SM_YVIRTUALSCREEN),
                    Interop.GetSystemMetrics(Interop.SM_CXVIRTUALSCREEN),
                    Interop.GetSystemMetrics(Interop.SM_CYVIRTUALSCREEN)));
            }

            // 纯数据比对模式：不起窗口，直接把 snapshot() 落盘
            if (DumpSnap != null)
            {
                var st = new Store(DataDir);
                File.WriteAllText(DumpSnap, Json.Stringify(st.Snapshot()), new UTF8Encoding(false));
                Log("  snapshot 已写出 -> " + DumpSnap);
                return;
            }

            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            Log("  [t=" + Boot.ElapsedMilliseconds + "ms] Application 已建");
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                // XAML 放在 src\ 下，编成 BAML 之后资源路径里也带着这一层
                Source = new Uri("pack://application:,,,/src/Theme.xaml", UriKind.Absolute),
            });
            Log("  [t=" + Boot.ElapsedMilliseconds + "ms] Theme.xaml 已加载");

            var win = new PanelWindow();
            Log("  [t=" + Boot.ElapsedMilliseconds + "ms] PanelWindow 已构造");
            app.MainWindow = win;
            app.Run(win);
        }

        /// <summary>数据目录已经就是 Electron 版那个，不需要再复制一份。</summary>
        static void PrepareDataDir()
        {
            Directory.CreateDirectory(DataDir);
        }

        /// <summary>
        /// 正常是 WinExe（不带控制台）；从终端重定向运行时标准输出仍然有效，
        /// 所以两边都写 —— 日志文件保证任何时候都拿得到。
        /// </summary>
        public static void Log(string s)
        {
            try { Console.WriteLine(s); Console.Out.Flush(); } catch (Exception) { }
            try { File.AppendAllText(LogPath, s + Environment.NewLine, new UTF8Encoding(false)); }
            catch (Exception) { }
        }
    }
}
