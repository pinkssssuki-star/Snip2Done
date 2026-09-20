using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace RemeberEr
{
    /// <summary>
    /// macOS 版入口（Avalonia）。
    ///
    /// 与 WPF 版的 Program.cs 对应，差异集中在三处：
    ///   1. 不需要 SetProcessDpiAwarenessContext —— mac 的缩放由 AppKit 处理，
    ///      代码里只要用 TopLevel.RenderScaling 做换算即可（见 Mvvm.Dpi）。
    ///   2. 数据目录从 %APPDATA%\remeber-er 改成 ~/Library/Application Support/remeber-er。
    ///   3. 命令行参数保持**完全一致**，这样 README 里的验收流程（--flow / --shot / --bench）
    ///      在两边可以照着同一份文档跑。
    /// </summary>
    public static class Program
    {
        /// <summary>发布版本号。与 Windows 版保持一致，不要各写一份。</summary>
        public const string Version = "0.9.1";

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
        public static string Flow;
        public static string AutoChoose;
        public static string ChooserShot;
        public static int AutoRegionW;
        public static int AutoRegionH;
        public static string OverlayShot;
        public static bool SynthDrag;
        public static bool DpiDiag;
        public static int ShotPad = 80;
        public static bool ForceQuick;
        public static string LogPath;

        /// <summary>
        /// 只申请权限然后退出，不起窗口。
        /// 用途：macOS 的屏幕录制权限授权后**通常要重启进程才生效**，
        /// 所以「申请」和「使用」必须能分成两次运行，不能揉在一个流程里。
        /// </summary>
        public static bool RequestPerms;

        /// <summary>
        /// 抓一次屏存成 PNG 然后退出，不起窗口。
        /// 用途：在搭框选层之前，先把「抓屏到底拿到的是不是正确内容」单独验证掉 ——
        /// 否则一旦框选层行为不对，无法判断是抓屏错了还是 UI 错了。
        /// </summary>
        public static string GrabPng;

        /// <summary>
        /// 框选层自检：抓屏 → 直接弹出框选层 → 自动画一个选区 → 存图 → 退出。
        /// 用途：把「框选层渲染得对不对」单独验证掉，不必先打通热键和归类窗。
        /// 参数值是要写出的 PNG 路径前缀。
        /// </summary>
        public static string OverlayTest;

        /// <summary>
        /// 只构造窗口、不显示、不抓屏，然后退出。
        /// 用途：不需要任何系统权限就能验证 XAML 能否解析、FindControl 是否都能找到 ——
        /// 这类错误在 GUI 里表现为「窗口一闪没了」，只看日志很难定位。
        /// </summary>
        public static bool XamlSelfTest;

        /// <summary>灌一份演示数据后退出（不碰真实数据，配合 --data= 用）。</summary>
        public static bool Seed;

        /// <summary>
        /// 把面板窗口自身渲染成 PNG 后退出（不需要屏幕录制权限）。
        /// 用途：让「排版对不对」这件事可以被直接看见/比对，
        /// 而不是只能靠人肉描述 —— GUI 移植里排版问题最耗来回沟通。
        /// </summary>
        public static string ShotPanel;

        /// <summary>把归类窗渲染成 PNG 后退出（免权限）。</summary>
        public static string ShotChooser;

        /// <summary>把详情窗渲染成 PNG 后退出（免权限）。</summary>
        public static string ShotDetail;

        /// <summary>跑一遍贴边隐藏的几何判定并打印结果后退出（免权限）。</summary>
        public static bool DockTest;

        /// <summary>热键自检：核对注册与派发（免权限）。</summary>
        public static bool HotkeyTest;

        /// <summary>主题自检：三个主题各渲染一张 PNG（免权限）。值是输出路径前缀。</summary>
        public static string ThemeTest;

        /// <summary>
        /// 渲染时切到哪个标签页（todo / want）。配合 --view=bin|done 用，
        /// 好把两个标签页分别渲染出来核对。
        /// </summary>
        public static string Tab;

        /// <summary>渲染时把倒垃圾按钮设成「已进入二次确认」，用于核对那个危险态的样子。</summary>
        public static bool ConfirmTrash;

        /// <summary>位置自检：置顶与折叠都不该改变窗口位置（免权限）。</summary>
        public static bool PosTest;

        /// <summary>补截图自检：给第一条条目补一张图（需要屏幕录制权限）。</summary>
        public static bool AttachTest;

        /// <summary>自动验收时给「两三天」那一档填的天数（0 = 不填，走 48~72 小时随机）。</summary>
        public static int AutoDays;

        /// <summary>多窗自检：详情窗/记录窗能不能同时开好几扇、重复点会不会开重复的。</summary>
        public static bool MultiWindowTest;

        /// <summary>渲染时把演示横幅设成某个状态：running / finished。用于核对横幅外观。</summary>
        public static string DemoBanner;

        /// <summary>渲染手动模式的时间面板（归类窗的两个按钮版本）。</summary>
        public static bool ManualChooser;

        /// <summary>手动记一笔自检：填标题 → 点加号 → 时间面板 → 选一个 → 落盘。</summary>
        public static bool ManualAddTest;

        /// <summary>热键接收自检：合成一次 ⌘⇧A，看全局监听能否收到。</summary>
        public static bool HotkeyRecvTest;

        /// <summary>
        /// 基准模式。**注意**：实际测量不走这个开关（见 build-app.sh 末尾的说明），
        /// 这里保留只是因为命令行参数要与 Windows 版兼容、不至于报未知参数。
        /// </summary>
        public static bool BenchMode;

        /// <summary>把完成记录窗渲染成 PNG 后退出（免权限）。值形如 "write:/path" 或 "view:/path"。</summary>
        public static string ShotRecord;

        /// <summary>
        /// 只跑数据 → ViewModel 这条链路并打印结果后退出，不起窗口。
        /// 用途：把「JSON 解析出来的分组/排序/文案对不对」和「窗口画得对不对」分开验证。
        /// 对应 P2 的验收标准。
        /// </summary>
        public static bool VmSelfTest;

        /// <summary>平台实现。P1 是桩，P3 起换成 MacPlatform。</summary>
        public static IPlatform Platform;

        public static readonly Stopwatch Boot = Stopwatch.StartNew();

        public static void Main(string[] args)
        {
            ParseArgs(args);

            if (Root == null) Root = AppContext.BaseDirectory;

            if (DataDir == null) DataDir = DefaultDataDir();

            // ⚠️ 单实例判定要放在**碰日志和数据目录之前**。
            // 第二个实例不该留下任何痕迹 —— 上游那边的原话是
            // 「原来连日志都会被它清掉」（每次启动都重开一份日志）。
            // 所以这一段必须在删日志、建目录、写任何东西之前跑完。
            if (!SingleInstance.TryAcquire(DataDir))
            {
                // 已经把正在跑的那个叫醒了，自己悄悄退出
                Console.Error.WriteLine("记忆面板已经在运行了 —— 已把它的面板叫到眼前，本次启动退出。");
                return;
            }

            try { Directory.CreateDirectory(DataDir); } catch (Exception) { }
            LogPath = Path.Combine(DataDir, "native.log");
            try { File.Delete(LogPath); } catch (Exception) { }

            Platform = CreatePlatform();

            Log("记忆面板 v" + Version + "（macOS / Avalonia）" + (Bench ? "（基准测试）" : ""));
            Log("  程序目录 = " + Root);
            Log("  数据目录 = " + DataDir);
            Log("  平台层   = " + Platform.Name);
            LogDisplayDiag();

            // 纯数据比对模式：不起窗口，直接把 snapshot() 落盘。
            // 和 Windows 版同一条命令，便于两边 diff 数据层行为。
            if (DumpSnap != null)
            {
                var st = new Store(DataDir);
                File.WriteAllText(DumpSnap, Json.Stringify(st.Snapshot()), new UTF8Encoding(false));
                Log("  snapshot 已写出 -> " + DumpSnap);
                return;
            }

            // 只申请权限、不起窗口。授权后必须重启进程才生效，
            // 所以这一步单独跑一次，再跑正常流程。
            if (RequestPerms)
            {
                Log("=== 申请系统权限 ===");
                bool scr = Platform.HasScreenCapturePermission();
                Log("  屏幕录制（当前）=" + (scr ? "已授权" : "未授权"));
                if (!scr)
                {
                    Log("  正在触发屏幕录制授权弹窗…");
                    Platform.RequestScreenCapturePermission();
                    Log("  已触发。请在系统设置里勾选本程序，然后**重启本进程**。");
                }
                // 辅助功能与输入监控：这两个也是全局热键要用的，
                // 缺了会表现为「热键注册成功但按下去没反应」
                bool ax = Platform.HasAccessibilityPermission();
                Log("  辅助功能（当前）=" + (ax ? "已授权" : "未授权"));
                if (!ax)
                {
                    Log("  正在触发辅助功能授权弹窗…");
                    Platform.RequestAccessibilityPermission();
                }

                bool im = HasInputMonitoring();
                Log("  输入监控（当前）=" + (im ? "已授权" : "未授权"));
                if (!im)
                {
                    Log("  正在触发输入监控授权弹窗…");
                    var mp = Platform as MacPlatform;
                    if (mp != null) mp.RequestInputMonitoringPermission();
                }

                Log("");
                Log("  ⚠ 授权后**必须重启本进程**才生效（macOS 的既定行为）。");
                Log("  ⚠ 全局热键需要「辅助功能」或「输入监控」其中至少一项：");
                Log("       Carbon 后端要辅助功能；NSEvent 后端要输入监控。");
                Log("=== 结束（本次不起窗口）===");
                return;
            }

            // 抓一次屏存 PNG 就退出 —— 用来隔离验证「抓屏」这一环。
            if (GrabPng != null)
            {
                Log("=== 抓屏自检 ===");
                var ds = Platform.GetDisplays();
                if (ds.Count == 0) { Log("!! 没有可用显示器"); return; }
                var d = ds[0];
                foreach (var cand in ds) if (cand.IsPrimary) d = cand;
                Log("  目标屏: " + d);

                var sw = System.Diagnostics.Stopwatch.StartNew();
                var shot = Platform.CaptureDisplay(d);
                sw.Stop();
                if (shot == null) { Log("!! 抓屏返回空，见上面日志的原因"); return; }

                Log(string.Format("  抓取尺寸 = {0}x{1}，用时 {2}ms", shot.Width, shot.Height, sw.ElapsedMilliseconds));

                // 采几个点看看内容，验证不是纯黑/纯色
                Log("  抽样像素: " + SamplePixels(shot, 4));

                bool ok = MacInterop.WritePng(GrabPng, shot.Width, shot.Height, shot.Bgra);
                Log((ok ? "  ✓ 已写出 -> " : "  !! 写出失败 -> ") + GrabPng);
                return;
            }

            // 数据 → ViewModel 链路自检
            if (VmSelfTest) { RunVmSelfTest(); return; }

            // 端到端链路自检：喂一张合成位图走完 框选 → 归类 → 落盘 → 刷新
            if (Flow == "chain") { RunFlowChain(); return; }

            // 实时端到端：从**真实屏幕**走完整条链路（需要屏幕录制权限）
            if (Flow == "live") { RunFlowLive(); return; }

            // 把面板自身渲染成 PNG（免权限）
            if (ShotPanel != null) { RunShotPanel(); return; }

            // 把归类窗渲染成 PNG（免权限）
            // 注意：--shot-chooser 这个分支在 --manual-chooser 之前，
            // 两者同时给的时候（用 --shot-chooser 指定输出路径 + --manual-chooser 切模式）
            // 必须在这里分流，否则下面那个独立分支永远轮不到（踩过）。
            if (HotkeyRecvTest) { RunHotkeyRecvTest(); return; }

            if (ManualAddTest) { RunManualAddTest(); return; }

            if (ShotChooser != null)
            {
                if (ManualChooser) RunManualChooserShot(); else RunShotChooser();
                return;
            }

            // 多窗自检
            if (MultiWindowTest) { RunMultiWindowTest(); return; }

            // 补截图自检
            if (AttachTest) { RunAttachTest(); return; }

            // 位置自检（免权限）
            if (PosTest) { RunPosTest(); return; }

            // 主题自检（免权限）
            if (ThemeTest != null) { RunThemeTest(); return; }

            // 热键自检（免权限）
            if (HotkeyTest) { RunHotkeyTest(); return; }

            // 贴边隐藏几何自检（免权限）
            if (DockTest) { RunDockTest(); return; }

            // 把详情窗渲染成 PNG（免权限）
            if (ShotDetail != null) { RunShotDetail(); return; }

            // 把完成记录窗渲染成 PNG（免权限）
            if (ShotRecord != null) { RunShotRecord(); return; }

            // 灌演示数据后退出（配合 --data= 指到临时目录，别污染真实数据）
            if (Seed)
            {
                Log("=== 灌演示数据 ===");
                var st = new Store(DataDir);
                st.SeedDemo();
                Log("  已灌入，条目数 = " + st.Items.Count);
                Log("  数据目录 = " + DataDir);
                return;
            }

            // 框选层自检：抓屏 → 弹框选层 → 自动画选区 → 存图 → 退出
            if (OverlayTest != null) { RunOverlayTest(); return; }

            // 不需要权限的 XAML 自检
            if (XamlSelfTest) { RunXamlSelfTest(); return; }

            AppMain();
        }

        /// <summary>
        /// 选平台实现。
        ///
        /// 这个 mac 项目按设计只会在 macOS 上跑（Avalonia 的 macOS 后端），
        /// 但仍然显式判断一次而不是直接 new MacPlatform()：
        /// 万一有人在别的系统上误跑，能拿到一句明确的说明，
        /// 而不是一串 DllNotFoundException 或「全是黑图」。
        /// 非 mac 上退回 StubPlatform，界面能起来，日志里能看出平台层是假的。
        /// </summary>
        static IPlatform CreatePlatform()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                return new MacPlatform();

            Log("!! 当前系统不是 macOS，平台层退回 stub —— 抓屏/热键/窗口探测都不会工作");
            return new StubPlatform();
        }

        /// <summary>
        /// 把面板窗口渲染成 PNG。
        /// 走 RenderTargetBitmap 直接抓视觉树，不经系统抓屏 —— 因此不需要任何权限，
        /// 在授权之前就能验证排版。
        /// </summary>
        /// <summary>
        /// 实时端到端：从真实屏幕抓图，走完 框选 → 归类 → 落盘 → 刷新。
        ///
        /// 与 --flow=chain 的区别只有一点：不喂合成位图，真的去抓屏。
        /// 这是目标里「在本机实际运行验证核心流程」那一句的正面回答。
        /// 用法：--flow=live --autoregion=WxH --autochoose=today|soon|want
        /// </summary>
        static void RunFlowLive()
        {
            Log("=== 实时端到端（真实屏幕）===");
            if (!Program.Platform.HasScreenCapturePermission())
            {
                Log("!! 缺少「屏幕录制」权限，无法做实时验证");
                Log("   系统设置 → 隐私与安全性 → 屏幕录制 → 勾选本程序 → 重启");
                Environment.Exit(1);
            }
            if (AutoRegionW <= 0) { AutoRegionW = 1200; AutoRegionH = 800; }
            if (AutoChoose == null) AutoChoose = "today";
            Log(string.Format("  选区 = {0}x{1}，归类 = {2}",
                AutoRegionW, AutoRegionH, AutoChoose));

            AppBuilder.Configure<SelfTestApp>()
                .UsePlatformDetect()
                .WithInterFont()
                .LogToTrace()
                .SetupWithoutStarting();

            Dispatcher.UIThread.Invoke(() =>
            {
                try
                {
                    var store = new Store(DataDir);
                    int before = store.Items.Count;

                    var panel = new PanelWindow();
                    panel.Show();
                    PumpUi(500);
                    Log("  面板已就绪，落盘前条目数 = " + before);

                    // 真的去抓屏、真的走框选层与归类窗
                    var controller = new CaptureController(store, panel);
                    controller.LiveCapture("实时验收");

                    PumpUi(3000);

                    var after = new Store(DataDir);
                    Log(string.Format("  落盘后条目数 = {0}（新增 {1}）",
                        after.Items.Count, after.Items.Count - before));

                    // 校验「最新那条」的图片是不是真有一块内容（不是全黑）
                    string newestId = null; double newestTs = -1;
                    foreach (var o in after.Items)
                    {
                        var it = J.Obj(o);
                        double ts = J.Num(it, "createdAt", 0);
                        if (ts > newestTs) { newestTs = ts; newestId = J.Str(it, "id", null); }
                    }
                    if (newestId != null)
                    {
                        var it = after.Get(newestId);
                        Log(string.Format("  最新条目：room={0} bucket={1} title=「{2}」 source=「{3}」",
                            J.Str(it, "room", ""), J.Str(it, "dueBucket", "—"),
                            J.Str(it, "title", ""), J.Str(it, "source", "")));
                        string rel = J.Str(it, "image", null);
                        if (!string.IsNullOrEmpty(rel))
                        {
                            string abs = System.IO.Path.Combine(DataDir, rel.Replace('/', System.IO.Path.DirectorySeparatorChar));
                            if (System.IO.File.Exists(abs))
                                Log(string.Format("  图片 = {0} 字节", new System.IO.FileInfo(abs).Length));
                            else Log("  !! 图片文件不存在");
                        }
                    }
                    Log("=== 实时端到端结束 ===");
                }
                catch (Exception ex)
                {
                    Log("!! 实时端到端失败：" + ex.GetType().Name + " " + ex.Message);
                }
            });

            Environment.Exit(0);
        }

        /// <summary>
        /// 端到端链路自检（**不需要屏幕录制权限**）。
        ///
        /// 做法：自己造一张合成位图喂给 CaptureController.RunFromScreenshot，
        /// 于是「框选层 → 归类窗 → 落盘 → 面板刷新」这整段都被真实驱动一遍，
        /// 只有「从屏幕抓图」那一步被换掉 —— 而那是整条链路里唯一需要权限的步骤。
        ///
        /// 用法：--flow=chain --autoregion=900x560 --autochoose=soon
        /// 之后用 --dumpsnap 复查落盘结果即可。
        /// </summary>
        static void RunFlowChain()
        {
            Log("=== 端到端链路自检（合成位图，免权限）===");
            if (AutoRegionW <= 0) { AutoRegionW = 900; AutoRegionH = 560; }
            if (AutoChoose == null) AutoChoose = "soon";
            Log(string.Format("  选区 = {0}x{1}，归类 = {2}", AutoRegionW, AutoRegionH, AutoChoose));

            var app = AppBuilder.Configure<SelfTestApp>()
                .UsePlatformDetect()
                .WithInterFont()
                .LogToTrace()
                .SetupWithoutStarting();

            Dispatcher.UIThread.Invoke(() =>
            {
                int before = -1;
                try
                {
                    var store = new Store(DataDir);
                    before = store.Items.Count;

                    var panel = new PanelWindow();
                    panel.Show();
                    PumpUi(600);
                    Log("  面板已就绪，落盘前条目数 = " + before);

                    // 合成一张「屏幕」位图：渐变 + 几个色块，便于肉眼确认裁出来的确实是选区
                    var disp = panel.Vm != null
                        ? new DisplayInfo { Id = 1, X = 0, Y = 0, W = 2560, H = 1440, Scale = 1, IsPrimary = true }
                        : null;
                    var shot = MakeSyntheticShot((int)disp.W, (int)disp.H);

                    var fg = new ForegroundInfo { Source = "自检应用", Title = "自检窗口标题" };

                    // 走真实链路：框选层 → 归类窗 → 落盘
                    var controller = new CaptureController(store, panel);
                    controller.RunFromScreenshot(shot, disp, fg, "自检");

                    // 让框选层的自动选区、归类窗的自动选择都跑完
                    PumpUi(2500);

                    var after = new Store(DataDir);
                    Log(string.Format("  落盘后条目数 = {0}（新增 {1}）",
                        after.Items.Count, after.Items.Count - before));

                    // 打印最新那条，核对 room / dueBucket / 标题 / 图片来源
                    string newestId = null;
                    double newestTs = -1;
                    foreach (var o in after.Items)
                    {
                        var it = J.Obj(o);
                        double ts = J.Num(it, "createdAt", 0);
                        if (ts > newestTs) { newestTs = ts; newestId = J.Str(it, "id", null); }
                    }
                    if (newestId != null)
                    {
                        var it = after.Get(newestId);
                        Log(string.Format("  最新条目：room={0} bucket={1} state={2}",
                            J.Str(it, "room", ""), J.Str(it, "dueBucket", "—"), J.Str(it, "state", "")));
                        Log(string.Format("             title=「{0}」 source=「{1}」 image={2}",
                            J.Str(it, "title", ""), J.Str(it, "source", ""), J.Str(it, "image", "—")));

                        // 图片真的写出来了吗？大小对不对？
                        string rel = J.Str(it, "image", null);
                        if (!string.IsNullOrEmpty(rel))
                        {
                            string abs = System.IO.Path.Combine(DataDir, rel.Replace('/', System.IO.Path.DirectorySeparatorChar));
                            if (System.IO.File.Exists(abs))
                            {
                                var fi = new System.IO.FileInfo(abs);
                                Log(string.Format("  图片文件 = {0}（{1} 字节）", abs, fi.Length));
                            }
                            else Log("  !! 图片文件不存在：" + abs);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log("!! 链路自检失败：" + ex.GetType().Name + " " + ex.Message);
                }
            });

            Environment.Exit(0);
        }

        static void RunShotPanel()
        {
            Log("=== 面板渲染自检 ===");
            var app = AppBuilder.Configure<SelfTestApp>()
                .UsePlatformDetect()
                .WithInterFont()
                .LogToTrace()
                .SetupWithoutStarting();

            Dispatcher.UIThread.Invoke(() =>
            {
                try
                {
                    var panel = new PanelWindow();
                    // 设标签页要在 Show 之前和之后各一次：
                    // 之前是为了让首次布局就用对的标签页，之后是防 Refresh 把它重置。
                    // 标签页要在 Show 之前设（否则首次布局用的是默认页）；
                    // 演示横幅则必须在 Show **之后** —— StartDemo 会切视图并刷新，
                    // 放在前面会被 Opened 里的 Refresh 冲掉（踩过：横幅不出现）。
                    if (!string.IsNullOrEmpty(Tab)) panel.Vm.BinTab = panel.Vm.DoneTab = Tab;
                    panel.Show();
                    if (!string.IsNullOrEmpty(Tab)) panel.Vm.BinTab = panel.Vm.DoneTab = Tab;
                    if (ConfirmTrash) panel.Vm.ConfirmingEmpty = true;
                    if (DemoBanner == "finished") { panel.Vm.DemoFinished = true; panel.Vm.DemoWasFirst = true; }
                    else if (DemoBanner == "running") panel.Vm.StartDemo();

                    // 主动把排队的 UI 任务跑完。
                    //
                    // ⚠️ 关键点：这里没有消息循环（用的是 SetupWithoutStarting），
                    // 所以 DispatcherTimer 和后台线程回调都**不会**被派发 ——
                    // 试过这两种写法，回调永远不触发，进程就那么挂着。
                    // RunJobs() 是在当前线程上同步排空队列，正好适合自检场景。
                    PumpUi(1200);

                    // 用布局后的实际尺寸，而不是 Window.Width/Height ——
                    // 后者是设置值，前者才是真正排版出来的大小。
                    int w = (int)Math.Ceiling(panel.Bounds.Width);
                    int h = (int)Math.Ceiling(panel.Bounds.Height);
                    if (w <= 0 || h <= 0) { w = (int)panel.Width; h = (int)panel.Height; }

                    var rtb = new Avalonia.Media.Imaging.RenderTargetBitmap(
                        new PixelSize(w, h), new Vector(96, 96));
                    rtb.Render(panel);
                    using (var fs = System.IO.File.Create(ShotPanel))
                        rtb.Save(fs, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());

                    Log(string.Format("  已写出 {0}（{1}x{2}）", ShotPanel, w, h));

                    // 量一下各区域实际拿到的尺寸，别靠猜
                    foreach (var n in new[] { "TodoSection", "WantSection" })
                    {
                        var c = panel.FindControl<Control>(n);
                        Log(string.Format("  布局 [{0}] 尺寸 = {1}x{2}",
                            n, c?.Bounds.Width, c?.Bounds.Height));
                    }

                    // 磁贴的实际测量高度 vs 它被分到的高度 —— 挤出磁贴的根因就在这个差值
                    // 注意：不能用 System.Linq 的 OfType —— Avalonia.Styling 里也有个
                    // 同名的 Selector 扩展方法，会把调用解析错（踩过编译错误）。
                    Avalonia.Controls.Border tile = null;
                    foreach (var v in panel.GetVisualDescendants())
                    {
                        var b = v as Avalonia.Controls.Border;
                        if (b != null && b.Classes.Contains("Tile")) { tile = b; break; }
                    }
                    if (tile != null)
                    {
                        Log(string.Format("  磁贴: 实际高={0:0.#} 欲高={1:0.#} 欲宽={2:0.#}",
                            tile.Bounds.Height, tile.DesiredSize.Height, tile.DesiredSize.Width));
                    }

                    // 把关键元素的精确矩形打印出来。
                    // 肉眼看渲染图判断「谁被裁了」很容易读错（一条灰线可能是滚动条），
                    // 数字不会骗人。
                    Log("  --- 关键元素矩形（相对各自父级）---");
                    DumpRects(panel, "Label");       // 副标题
                    DumpRects(panel, "LifeBar");     // 生命条
                    DumpRects(panel, "Tile");        // 磁贴
                    Log(string.Format("  内容：Todo {0} / Wantdo {1} / 纸篓 {2} / 完成组 {3}",
                        panel.Vm.Todos.Count, panel.Vm.Wants.Count,
                        panel.Vm.Bins.Count, panel.Vm.DoneGroups.Count));
                }
                catch (Exception ex)
                {
                    Log("!! 渲染失败：" + ex.GetType().Name + " " + ex.Message);
                }
            });

            Environment.Exit(0);
        }

        /// <summary>取数据里第一条条目的 id，给各窗口自检用。</summary>
        static string FirstItemId() { return FindItemId(null); }

        /// <summary>
        /// 找一条符合状态的条目 id。
        /// <paramref name="wantState"/> 为 null 时取第一条；否则取 state 匹配的第一条
        /// （完成记录窗的 view 模式必须要一条已完成的，否则渲染出来是空壳）。
        /// </summary>
        static string FindItemId(string wantState)
        {
            try
            {
                var st = new Store(DataDir);
                foreach (var o in st.Items)
                {
                    var it = J.Obj(o);
                    var id = J.Str(it, "id", null);
                    if (string.IsNullOrEmpty(id)) continue;
                    if (wantState != null && J.Str(it, "state", "") != wantState) continue;
                    return id;
                }
            }
            catch (Exception) { }
            return null;
        }

        /// <summary>
        /// 把详情窗渲染成 PNG（免权限）。
        /// 需要一个真实条目 —— 用 --data= 指向 --seed 造出来的演示数据即可。
        /// </summary>
        static void RunShotDetail()
        {
            Log("=== 详情窗渲染自检 ===");
            var id = FirstItemId();
            if (id == null) { Log("!! 数据里没有条目，先用 --seed 造一份"); Environment.Exit(1); }
            Log("  用条目 id = " + id);

            AppBuilder.Configure<SelfTestApp>()
                .UsePlatformDetect()
                .WithInterFont()
                .LogToTrace()
                .SetupWithoutStarting();

            Dispatcher.UIThread.Invoke(() =>
            {
                try
                {
                    var win = new DetailWindow(new Store(DataDir), id);
                    win.Show();
                    PumpUi(500);

                    int w = (int)Math.Ceiling(win.Bounds.Width);
                    int h = (int)Math.Ceiling(win.Bounds.Height);
                    var rtb = new Avalonia.Media.Imaging.RenderTargetBitmap(
                        new PixelSize(w, h), new Vector(96, 96));
                    rtb.Render(win);
                    using (var fs = System.IO.File.Create(ShotDetail))
                        rtb.Save(fs, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());

                    Log(string.Format("  已写出 {0}（{1}x{2}）", ShotDetail, w, h));
                    Log(string.Format("  按图片比例算出的窗口尺寸 = {0}x{1}（逻辑）", win.Width, win.Height));
                }
                catch (Exception ex)
                {
                    Log("!! 渲染失败：" + ex.GetType().Name + " " + ex.Message);
                }
            });

            Environment.Exit(0);
        }

        /// <summary>
        /// 把完成记录窗渲染成 PNG（免权限）。
        /// 参数形如 "write:/path/to.png" / "view:/path/to.png" —— 两种模式布局差别很大，
        /// 都得看。
        /// </summary>
        static void RunShotRecord()
        {
            int colon = ShotRecord.IndexOf(':');
            string mode = colon > 0 ? ShotRecord.Substring(0, colon) : "view";
            string path = colon > 0 ? ShotRecord.Substring(colon + 1) : ShotRecord;

            Log("=== 完成记录窗渲染自检（模式 " + mode + "）===");
            // view 模式得用一条真的已完成的条目，否则渲染出来是空壳
            var id = mode == "view" ? FindItemId("done") : FirstItemId();
            if (id == null) { Log("!! 数据里没有合适的条目，先用 --seed 造一份"); Environment.Exit(1); }
            Log("  用条目 id = " + id);

            AppBuilder.Configure<SelfTestApp>()
                .UsePlatformDetect()
                .WithInterFont()
                .LogToTrace()
                .SetupWithoutStarting();

            Dispatcher.UIThread.Invoke(() =>
            {
                try
                {
                    var win = new RecordWindow(new Store(DataDir), id, mode);
                    win.Show();
                    PumpUi(500);

                    int w = (int)Math.Ceiling(win.Bounds.Width);
                    int h = (int)Math.Ceiling(win.Bounds.Height);
                    var rtb = new Avalonia.Media.Imaging.RenderTargetBitmap(
                        new PixelSize(w, h), new Vector(96, 96));
                    rtb.Render(win);
                    using (var fs = System.IO.File.Create(path))
                        rtb.Save(fs, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());

                    Log(string.Format("  已写出 {0}（{1}x{2}）", path, w, h));
                }
                catch (Exception ex)
                {
                    Log("!! 渲染失败：" + ex.GetType().Name + " " + ex.Message);
                }
            });

            Environment.Exit(0);
        }

        /// <summary>
        /// 渲染手动模式的时间面板：应当是**两个按钮**（Today / 两三天），
        /// 没有缩略图、没有 Wantdo —— 手动记的是 Todo，而 Todo 必须有期限，
        /// 「无期限」在 Todo 语境下自相矛盾。
        /// </summary>
        /// <summary>
        /// 手动记一笔自检：模拟用户填标题、点加号、在时间面板里选一个，
        /// 核对落盘结果的 room / bucket / 标题 / 期限。
        ///
        /// 重点验的是「没期限的 Todo 是自相矛盾的」这一条 ——
        /// 手动记的 Todo 必须走选时间那一步，而不是直接落库。
        /// </summary>
        /// <summary>
        /// 热键接收自检：合成一次 ⌘⇧A，看 NSEvent 全局监听能不能收到。
        ///
        /// 意义：如果能收到，说明「全局监听看不到合成事件」这个顾虑不成立，
        /// 热键这条链就能在不麻烦真人的前提下自测；
        /// 如果收不到，那说明 NSEvent 只认真实硬件按键，
        /// 这类验证就只能靠人按 —— 两种结论都值得知道。
        /// </summary>
        static void RunHotkeyRecvTest()
        {
            Log("=== 热键接收自检（合成 ⌘⇧A）===");
            AppBuilder.Configure<SelfTestApp>()
                .UsePlatformDetect()
                .WithInterFont()
                .LogToTrace()
                .SetupWithoutStarting();

            Dispatcher.UIThread.Invoke(() =>
            {
                try
                {
                    var panel = new PanelWindow();
                    panel.Show();
                    PumpUi(600);

                    Log(string.Format("  权限：辅助功能={0} 输入监控={1}",
                        Program.Platform.HasAccessibilityPermission(),
                        HasInputMonitoring()));
                    Log("  合成一次 ⌘⇧A…");

                    // 合成要在 UI 线程泵之外做，否则监听回调没机会跑
                    System.Threading.Tasks.Task.Run(() =>
                    {
                        System.Threading.Thread.Sleep(500);
                        Dispatcher.UIThread.Post(() =>
                        {
                            MacHotkeys.PostTestHotkey();
                            // 合成之后主动泵一会儿，让 NSEvent 的回调有机会执行
                            PumpUi(1500);
                            Log(string.Format("  结果：命中 {0} 次 {1}",
                                MacHotkeys.HitCount,
                                MacHotkeys.HitCount > 0
                                    ? "→ 全局监听**能**看到合成事件，热键链可自测"
                                    : "→ 全局监听看不到合成事件（只认真实硬件按键），这类验证只能靠人按"));
                            Log("=== 热键接收自检结束 ===");
                        });
                    });
                }
                catch (Exception ex)
                {
                    Log("!! 自检失败：" + ex.GetType().Name + " " + ex.Message);
                }
            });

            // 给后台任务留出时间
            System.Threading.Thread.Sleep(4000);
            Environment.Exit(0);
        }

        static void RunManualAddTest()
        {
            Log("=== 手动记一笔自检 ===");
            AppBuilder.Configure<SelfTestApp>()
                .UsePlatformDetect()
                .WithInterFont()
                .LogToTrace()
                .SetupWithoutStarting();

            Dispatcher.UIThread.Invoke(() =>
            {
                try
                {
                    var store = new Store(DataDir);
                    int before = store.Items.Count;

                    var panel = new PanelWindow();
                    panel.Show();
                    PumpUi(400);

                    // 填标题 + 选 Todo 房间 + 点加号
                    panel.Vm.QuickText = "手动记的一条 Todo";
                    panel.Vm.SetRoomCmd.Execute("todo");
                    panel.Vm.QuickOpen = true;
                    PumpUi(200);
                    Log("  已填标题并选 Todo，准备点加号");

                    panel.StartManualAddForTest();
                    PumpUi(500);

                    // 时间面板应该开出来了：从 _kids 里找 ChooserWindow
                    ChooserWindow ch = null;
                    foreach (var w in panel.ChildrenForTest)
                    {
                        ch = w as ChooserWindow;
                        if (ch != null) break;
                    }
                    if (ch == null)
                    {
                        Log("  !! 点加号之后没有弹出时间面板 —— 这正是要修的问题");
                        return;
                    }
                    Log("  ✓ 时间面板已弹出（说明 Todo 走了选时间那一步）");

                    // 在里面选 Today
                    ch.AutoPick("today");
                    PumpUi(500);

                    var after = new Store(DataDir);
                    Log(string.Format("  落盘：条目 {0} -> {1}", before, after.Items.Count));
                    string newest = null; double ts = -1;
                    foreach (var o in after.Items)
                    {
                        var it = J.Obj(o);
                        double t = J.Num(it, "createdAt", 0);
                        if (t > ts) { ts = t; newest = J.Str(it, "id", null); }
                    }
                    var n = after.Get(newest);
                    Log(string.Format("  最新条目：room={0} bucket={1} title=「{2}」已有期限={3}",
                        J.Str(n, "room", ""), J.Str(n, "dueBucket", "—"), J.Str(n, "title", ""),
                        !J.IsNull(J.Get(n, "dueAt"))));
                    Log(string.Format("  输入框已清空 = {0}（应 True）", (panel.Vm.QuickText ?? "") == ""));
                    Log(string.Format("  输入区已收起 = {0}（应 True）", !panel.Vm.QuickOpen));
                    Log("=== 手动记一笔自检结束 ===");
                }
                catch (Exception ex)
                {
                    Log("!! 手动记一笔自检失败：" + ex.GetType().Name + " " + ex.Message);
                }
            });

            Environment.Exit(0);
        }

        static void RunManualChooserShot()
        {
            Log("=== 手动模式时间面板渲染 ===");
            AppBuilder.Configure<SelfTestApp>()
                .UsePlatformDetect()
                .WithInterFont()
                .LogToTrace()
                .SetupWithoutStarting();

            Dispatcher.UIThread.Invoke(() =>
            {
                try
                {
                    var disp = new DisplayInfo
                    {
                        Id = 1, X = 0, Y = 0, W = 2560, H = 1440, Scale = 1, IsPrimary = true,
                    };
                    var payload = new ChooserWindow.Payload
                    {
                        Title = "手动记一笔的标题",
                        Manual = true,
                    };
                    Log(string.Format("  [测试] 传入前 payload hash={0} Manual={1}",
                        payload.GetHashCode(), payload.Manual));
                    var w = new ChooserWindow(payload, disp, 1200, 600, 340, 520);
                    w.Show();
                    PumpUi(400);
                    Log("  摆放位置(物理) = " + w.Position);

                    int cw = (int)Math.Ceiling(w.Bounds.Width);
                    int ch = (int)Math.Ceiling(w.Bounds.Height);
                    var rtb = new Avalonia.Media.Imaging.RenderTargetBitmap(
                        new PixelSize(cw, ch), new Vector(96, 96));
                    rtb.Render(w);
                    string path = Program.ShotChooser ?? "/tmp/manual-chooser.png";
                    using (var fs = System.IO.File.Create(path))
                        rtb.Save(fs, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
                    Log(string.Format("  已写出 {0}（{1}x{2}）", path, cw, ch));
                }
                catch (Exception ex)
                {
                    Log("!! 渲染失败：" + ex.GetType().Name + " " + ex.Message);
                }
            });
            Environment.Exit(0);
        }

        /// <summary>
        /// 多窗自检：连开 3 扇详情窗应为 3 扇；重复点第 1 条仍是 3 扇
        /// （上游的验收标准就是这个）。记录窗同理。
        ///
        /// 上游那个「只能点开一个」的根因值得记一笔：它其实不是「只能开一个」，
        /// 而是**回归** —— 开着 A 时点 B 只把 A 提到前面、内容还是 A，
        /// 所以看起来像点了没反应。
        /// </summary>
        static void RunMultiWindowTest()
        {
            Log("=== 多窗自检 ===");
            AppBuilder.Configure<SelfTestApp>()
                .UsePlatformDetect()
                .WithInterFont()
                .LogToTrace()
                .SetupWithoutStarting();

            Dispatcher.UIThread.Invoke(() =>
            {
                try
                {
                    var store = new Store(DataDir);
                    // 取三个条目 id
                    var ids = new System.Collections.Generic.List<string>();
                    foreach (var o in store.Items)
                    {
                        var it = J.Obj(o);
                        var id = J.Str(it, "id", null);
                        if (!string.IsNullOrEmpty(id)) ids.Add(id);
                        if (ids.Count >= 3) break;
                    }
                    if (ids.Count < 3) { Log("!! 需要至少 3 条数据，先用 --seed 造一份"); return; }

                    var panel = new PanelWindow();
                    panel.Show();
                    PumpUi(500);

                    int CountDetails()
                    {
                        int n = 0;
                        foreach (var w in panel.ChildrenForTest) if (w is DetailWindow) n++;
                        return n;
                    }

                    foreach (var id in ids)
                    {
                        panel.OpenDetailForTest(id);
                        PumpUi(250);
                        // 错开位置要真的不一样，否则几扇完全叠着看不出开了多个
                        var last = panel.LastChildForTest;
                        Log(string.Format("    开第 {0} 扇 @ {1}  {2}x{3}",
                            CountDetails(), last != null ? last.Position.ToString() : "?", 
                            (int)Math.Round(last?.Width ?? 0), (int)Math.Round(last?.Height ?? 0)));
                    }
                    Log(string.Format("  连开 3 扇 -> 实际 {0} 扇（应 3）", CountDetails()));

                    // 重复点第 1 条
                    panel.OpenDetailForTest(ids[0]);
                    PumpUi(250);
                    Log(string.Format("  重复点第 1 条 -> 仍是 {0} 扇（应 3）", CountDetails()));

                    // 记录窗也验一遍
                    int CountRecords()
                    {
                        int n = 0;
                        foreach (var w in panel.ChildrenForTest) if (w is RecordWindow) n++;
                        return n;
                    }
                    panel.OpenRecordForTest(ids[0]);
                    PumpUi(250);
                    panel.OpenRecordForTest(ids[1]);
                    PumpUi(250);
                    panel.OpenRecordForTest(ids[0]);   // 重复
                    PumpUi(250);
                    Log(string.Format("  记录窗：开两条（其中一条重复点）-> {0} 扇（应 2）", CountRecords()));

                    Log("=== 多窗自检结束 ===");
                }
                catch (Exception ex)
                {
                    Log("!! 多窗自检失败：" + ex.GetType().Name + " " + ex.Message);
                }
            });

            Environment.Exit(0);
        }

        /// <summary>
        /// 补截图自检：造一条没有图的条目，然后走「补截图」流程给它挂一张。
        ///
        /// 走的是 CaptureController.StartCaptureForItem —— 也就是详情窗那个加号按下去的
        /// 同一条路径（含框选层、自动选区、挂图、刷新）。
        /// </summary>
        static void RunAttachTest()
        {
            Log("=== 补截图自检 ===");
            if (!Program.Platform.HasScreenCapturePermission())
            {
                Log("!! 缺少「屏幕录制」权限，无法做补截图验证");
                Environment.Exit(1);
            }
            if (AutoRegionW <= 0) { AutoRegionW = 400; AutoRegionH = 300; }

            AppBuilder.Configure<SelfTestApp>()
                .UsePlatformDetect()
                .WithInterFont()
                .LogToTrace()
                .SetupWithoutStarting();

            Dispatcher.UIThread.Invoke(() =>
            {
                try
                {
                    var store = new Store(DataDir);
                    // 造一条没有图的条目
                    var it = store.Add("todo", "补截图自检条目", "自检", null, "today");
                    string id = J.Str(it, "id", null);
                    Log("  目标条目 = " + id);
                    Log("  补图前 image = " + (J.Str(it, "image", null) ?? "(空)"));

                    var panel = new PanelWindow();
                    panel.Show();
                    PumpUi(400);

                    var ctl = new CaptureController(store, panel);
                    ctl.StartCaptureForItem(id);
                    PumpUi(2500);

                    var after = new Store(DataDir);
                    var it2 = after.Get(id);
                    string rel = it2 != null ? J.Str(it2, "image", null) : null;
                    Log("  补图后 image = " + (rel ?? "(还是空！)"));

                    if (!string.IsNullOrEmpty(rel))
                    {
                        string abs = System.IO.Path.Combine(DataDir,
                            rel.Replace('/', System.IO.Path.DirectorySeparatorChar));
                        if (System.IO.File.Exists(abs))
                            Log(string.Format("  图片文件 = {0} 字节 ✓", new System.IO.FileInfo(abs).Length));
                        else Log("  !! 图片文件不存在");
                    }
                    Log("=== 补截图自检结束 ===");
                }
                catch (Exception ex)
                {
                    Log("!! 补截图自检失败：" + ex.GetType().Name + " " + ex.Message);
                }
            });

            Environment.Exit(0);
        }

        /// <summary>
        /// 位置自检：把面板摆到一个既不是右下角也不是原点的位置，然后
        /// 依次点置顶 / 折叠 / 展开，核对位置始终不动。
        ///
        /// 对应上游 v0.9.1 修的两个缺陷（点置顶跳到左上角、点折叠甩到右下角）。
        /// 这两个症状都不影响功能，但用起来非常恼人，而且光看代码很难发现
        /// 「某处顺手调了一下摆窗口的函数」—— 必须真的摆出来量。
        /// </summary>
        static void RunPosTest()
        {
            Log("=== 位置自检（置顶 / 折叠 不该移动窗口）===");
            AppBuilder.Configure<SelfTestApp>()
                .UsePlatformDetect()
                .WithInterFont()
                .LogToTrace()
                .SetupWithoutStarting();

            Dispatcher.UIThread.Invoke(() =>
            {
                try
                {
                    var panel = new PanelWindow();
                    panel.Show();
                    PumpUi(500);

                    // 摆到一个既不是右下角也不是原点的位置，这样「被甩走」才看得出来
                    panel.Position = new Avalonia.PixelPoint(120, 90);
                    PumpUi(200);

                    string P() { return string.Format("({0},{1}) {2}x{3}",
                        panel.Position.X, panel.Position.Y,
                        (int)Math.Round(panel.Width), (int)Math.Round(panel.Height)); }

                    Log("  初始      " + P());

                    panel.Vm.ToggleTopmost(); PumpUi(200);
                    Log("  点置顶    " + P() + "　置顶=" + panel.Topmost);

                    panel.Vm.ToggleTopmost(); PumpUi(200);
                    Log("  再点置顶  " + P() + "　置顶=" + panel.Topmost);

                    panel.ToggleCollapseForTest(); PumpUi(300);
                    Log("  折叠      " + P());

                    panel.ToggleCollapseForTest(); PumpUi(300);
                    Log("  展开      " + P());

                    // 隐藏折叠按钮那条规则的核对
                    Log(string.Format("  隐藏折叠按钮开关：折叠着时可见={0}（应 True）",
                        panel.Vm.ShowCollapseButton));
                    panel.Vm.HideCollapseButton = true; PumpUi(150);
                    Log(string.Format("  设为隐藏后：未折叠可见={0}（应 False）", panel.Vm.ShowCollapseButton));
                    panel.ToggleCollapseForTest(); PumpUi(300);
                    Log(string.Format("  设为隐藏后：折叠着可见={0}（应 True，否则展不开）",
                        panel.Vm.ShowCollapseButton));
                    panel.Vm.HideCollapseButton = false;

                    Log("=== 位置自检结束 ===");
                }
                catch (Exception ex)
                {
                    Log("!! 位置自检失败：" + ex.GetType().Name + " " + ex.Message);
                }
            });

            Environment.Exit(0);
        }

        /// <summary>
        /// 主题自检：三个主题各渲染一张 PNG。
        ///
        /// 验的是「Palette.Apply 改到的是不是 StaticResource 解析出来的那个画刷实例」——
        /// 这是整个换主题机制唯一的命门。如果 Apply 改的是自己新建的画刷，
        /// 界面不会有任何变化，而只看代码是看不出来的，必须渲染出来对比。
        /// </summary>
        static void RunThemeTest()
        {
            Log("=== 主题自检 ===");
            AppBuilder.Configure<SelfTestApp>()
                .UsePlatformDetect()
                .WithInterFont()
                .LogToTrace()
                .SetupWithoutStarting();

            Dispatcher.UIThread.Invoke(() =>
            {
                try
                {
                    // 先把主题写进数据，再建面板。
                    // 必须这样：panel.Show() 触发 Refresh()，而 Refresh 会**从数据里读**
                    // panelTheme 并重新应用 —— 只调 Palette.Apply 的话会被它覆盖回旧值
                    // （踩过：黑色主题渲染出来仍是白色，查了半天才意识到是测试脚本的锅，
                    //   不是主题机制的问题）。真实路径下 GUI 改主题时会同步写 store，
                    // 所以不会出现这个现象。
                    var seedStore = new Store(DataDir);
                    seedStore.SetSetting("panelTheme", Palette.All[0].Key);
                    foreach (var e in Palette.All)
                    {
                        seedStore.SetSetting("panelTheme", e.Key);
                        Palette.Apply(e.Key);
                        PumpUi(150);
                        // 校验状态真的换了
                        var cur = Palette.Current;
                        Log(string.Format("  [校验] Apply({0}) 后 Palette.Current = {1} / Panel=#{2:X2}{3:X2}{4:X2}",
                            e.Key, cur.Key, cur.Panel.R, cur.Panel.G, cur.Panel.B));

                        var panel = new PanelWindow();
                        panel.Show();
                        PumpUi(600);

                        int w = (int)Math.Ceiling(panel.Bounds.Width);
                        int h = (int)Math.Ceiling(panel.Bounds.Height);

                        // 查外壳 Border 的实际底色 —— 渲染出来是浅色但状态说黑色时，
                        // 必须看这一步：到底是「没设上」还是「被绑定覆盖了」
                        var shell = panel.FindControl<Avalonia.Controls.Border>("Shell");
                        var sb = shell != null ? shell.Background as Avalonia.Media.SolidColorBrush : null;
                        Log(string.Format("    [外壳] Background = {0}",
                            sb == null ? "(null 或非纯色)" : sb.Color.ToString()));

                        // 关键诊断：界面上的文字颜色到底是不是主题色？
                        // 若 StaticResource 返回的不是 Palette 改的那个实例，
                        // 这里就会看到旧颜色（白主题的深色），换主题只有底色会变。
                        // 一次性的探针：StaticResource 与 DynamicResource 谁跟着变
                        var probe = new ResourceProbe();
                        Log("    [探针] 主题 " + e.Key + " -> " + probe.Report());

                        object res;
                        if (Application.Current.TryFindResource("Tx1", out res))
                        {
                            var rb = res as Avalonia.Media.SolidColorBrush;
                            Log(string.Format("    [资源 Tx1] {0}（主题期望 #{1:X2}{2:X2}{3:X2}）",
                                rb == null ? "(非纯色)" : rb.Color.ToString(),
                                Palette.Current.Tx1.R, Palette.Current.Tx1.G, Palette.Current.Tx1.B));
                        }
                        else Log("    [资源 Tx1] 找不到");

                        var rtb = new Avalonia.Media.Imaging.RenderTargetBitmap(
                            new PixelSize(w, h), new Vector(96, 96));
                        rtb.Render(panel);
                        string path = ThemeTest + "-" + e.Key + ".png";
                        using (var fs = System.IO.File.Create(path))
                            rtb.Save(fs, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());

                        Log(string.Format("  主题 {0}({1}) -> {2}（{3}x{4}）",
                            e.Key, e.Name, path, w, h));

                        // 顺便核对底色真的变了（不是三张一样的图）
                        var col = e.Panel;
                        Log(string.Format("    面板底色应为 #{0:X2}{1:X2}{2:X2}",
                            col.R, col.G, col.B));

                        panel.Close();
                    }
                    Log("=== 主题自检结束 ===");
                }
                catch (Exception ex)
                {
                    Log("!! 主题自检失败：" + ex.GetType().Name + " " + ex.Message);
                }
            });

            Environment.Exit(0);
        }

        /// <summary>热键自检（免权限）。</summary>
        static void RunHotkeyTest()
        {
            AppBuilder.Configure<SelfTestApp>()
                .UsePlatformDetect()
                .WithInterFont()
                .LogToTrace()
                .SetupWithoutStarting();

            Dispatcher.UIThread.Invoke(() =>
            {
                try
                {
                    var panel = new PanelWindow();
                    panel.Show();
                    PumpUi(400);
                    // 面板 Opened 里已注册热键，这里核对注册与派发
                    var store = new Store(DataDir);
                    var ctl = new CaptureController(store, panel);
                    ctl.HotkeySelfTest();
                }
                catch (Exception ex)
                {
                    Log("!! 热键自检异常：" + ex.GetType().Name + " " + ex.Message);
                }
            });

            Environment.Exit(0);
        }

        /// <summary>贴边隐藏几何自检（免权限）。</summary>
        static void RunDockTest()
        {
            AppBuilder.Configure<SelfTestApp>()
                .UsePlatformDetect()
                .WithInterFont()
                .LogToTrace()
                .SetupWithoutStarting();

            Dispatcher.UIThread.Invoke(() =>
            {
                try
                {
                    var panel = new PanelWindow();
                    panel.Show();
                    PumpUi(400);
                    panel.DockSelfTest();
                }
                catch (Exception ex)
                {
                    Log("!! 贴边自检异常：" + ex.GetType().Name + " " + ex.Message);
                }
            });

            Environment.Exit(0);
        }

        /// <summary>把带某个样式类的元素矩形打出来，用于核对布局是否溢出。</summary>
        static void DumpRects(Avalonia.Controls.Control root, string cls)
        {
            int i = 0;
            foreach (var v in root.GetVisualDescendants())
            {
                var c = v as Avalonia.Controls.Control;
                if (c == null || !c.Classes.Contains(cls)) continue;
                Log(string.Format("    {0}[{1}] 尺寸={2:0.#}x{3:0.#} 位置=({4:0.#},{5:0.#}) 可见={6}",
                    cls, i++, c.Bounds.Width, c.Bounds.Height,
                    c.Bounds.X, c.Bounds.Y, c.IsVisible));
            }
        }

        /// <summary>
        /// 把归类窗渲染成 PNG。
        /// 用一个纯色小图当预览占位 —— 归类窗的排版跟截图内容无关，
        /// 免权限自检不该依赖抓屏。
        /// </summary>
        static void RunShotChooser()
        {
            Log("=== 归类窗渲染自检 ===");
            AppBuilder.Configure<SelfTestApp>()
                .UsePlatformDetect()
                .WithInterFont()
                .LogToTrace()
                .SetupWithoutStarting();

            Dispatcher.UIThread.Invoke(() =>
            {
                try
                {
                    var disp = new DisplayInfo
                    {
                        Id = 1, X = 0, Y = 0, W = 2560, H = 1440, Scale = 1, IsPrimary = true,
                    };

                    // 造一张渐变占位图，让缩略图那一格有东西可看
                    var placeholder = MakePlaceholder(96, 64);

                    var win = new ChooserWindow(new ChooserWindow.Payload
                    {
                        Preview = placeholder,
                        App = "Google Chrome",
                        Title = "定价页文案 · 修改建议",
                    }, disp, 700, 400, 900, 560);

                    win.Show();
                    PumpUi(400);

                    int w = (int)Math.Ceiling(win.Bounds.Width);
                    int h = (int)Math.Ceiling(win.Bounds.Height);
                    var rtb = new Avalonia.Media.Imaging.RenderTargetBitmap(
                        new PixelSize(w, h), new Vector(96, 96));
                    rtb.Render(win);
                    using (var fs = System.IO.File.Create(ShotChooser))
                        rtb.Save(fs, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());

                    Log(string.Format("  已写出 {0}（{1}x{2}）", ShotChooser, w, h));
                    Log(string.Format("  摆放位置(物理) = {0}", win.Position));
                }
                catch (Exception ex)
                {
                    Log("!! 渲染失败：" + ex.GetType().Name + " " + ex.Message);
                }
            });

            Environment.Exit(0);
        }

        /// <summary>
        /// 造一张合成的「屏幕截图」（BGRA 缓冲），用于免权限的链路自检。
        ///
        /// 图里画了明显的参考物：左上角一个红块、右上角一个蓝块、中间一条白线。
        /// 这样把裁出来的选区存成 PNG 之后，一眼就能确认裁的位置对不对 ——
        /// 纯渐变看不出坐标偏移。
        /// </summary>
        static Screenshot MakeSyntheticShot(int w, int h)
        {
            var bgra = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int o = (y * w + x) * 4;
                    // 背景：斜向渐变
                    bgra[o] = (byte)(120 + 90 * x / Math.Max(1, w));       // B
                    bgra[o + 1] = (byte)(110 + 70 * y / Math.Max(1, h));   // G
                    bgra[o + 2] = (byte)(190 - 70 * x / Math.Max(1, w));   // R
                    bgra[o + 3] = 255;

                    // 左上角红块（BGR 顺序：蓝=0 绿=0 红=255）
                    if (x < w / 8 && y < h / 8) { bgra[o] = 0; bgra[o + 1] = 0; bgra[o + 2] = 255; }
                    // 右上角蓝块
                    if (x >= w - w / 8 && y < h / 8) { bgra[o] = 255; bgra[o + 1] = 0; bgra[o + 2] = 0; }
                    // 正中间一条白线
                    if (Math.Abs(y - h / 2) < 3) { bgra[o] = 255; bgra[o + 1] = 255; bgra[o + 2] = 255; }
                }
            }
            return new Screenshot { Width = w, Height = h, Bgra = bgra };
        }

        /// <summary>造一张渐变占位图，用于免权限的渲染自检。</summary>
        static Avalonia.Media.Imaging.Bitmap MakePlaceholder(int w, int h)
        {
            var wb = new Avalonia.Media.Imaging.WriteableBitmap(
                new PixelSize(w, h), new Vector(96, 96),
                Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Opaque);
            using (var fb = wb.Lock())
            {
                unsafe
                {
                    var basePtr = (byte*)fb.Address;
                    for (int y = 0; y < h; y++)
                    {
                        var row = basePtr + y * fb.RowBytes;
                        for (int x = 0; x < w; x++)
                        {
                            var px = row + x * 4;
                            px[0] = (byte)(120 + 100 * x / Math.Max(1, w));   // B
                            px[1] = (byte)(90 + 60 * y / Math.Max(1, h));     // G
                            px[2] = (byte)(200 - 60 * x / Math.Max(1, w));    // R
                            px[3] = 255;
                        }
                    }
                }
            }
            return wb;
        }

        /// <summary>
        /// 在当前线程上把 UI 任务队列排空指定的毫秒数。
        /// 自检专用：没有消息循环时，这是唯一能让绑定求值、布局、异步回填跑起来的方式。
        /// </summary>
        static void PumpUi(int ms)
        {
            var until = Environment.TickCount64 + ms;
            while (Environment.TickCount64 < until)
            {
                Dispatcher.UIThread.RunJobs();
                System.Threading.Thread.Sleep(10);
            }
        }

        /// <summary>
        /// 跑一遍 PanelViewModel.Refresh()，把解析结果全部打印出来。
        /// 同时刻意打印几个「JSON 里是 null」的字段的处理结果 ——
        /// 演示数据里 decayCfg / stats 就是 null，这是最容易漏的边界。
        /// </summary>
        static void RunVmSelfTest()
        {
            Log("=== ViewModel 自检 ===");
            var store = new Store(DataDir);
            var vm = new PanelViewModel(store);
            vm.Refresh();

            Log(string.Format("  Todo {0} 条（进行中 {1}） / Wantdo {2} 条 / 纸篓 {3} 条 / 完成 {4} 条",
                vm.Todos.Count, vm.NTodo, vm.Wants.Count, vm.Bins.Count, vm.NDone));
            Log(string.Format("  Stats = {0}", vm.Stats));
            Log(string.Format("  设置: 透明度={0} 快速模式={1} 置顶={2} 演示加速={3}",
                vm.OpacityText, vm.QuickCapture, vm.AlwaysOnTop, vm.DemoOn));
            Log(string.Format("  衰减配置: 保护期={0} 放手期={1}（JSON 里是 null，应回落成默认 7/21）",
                vm.FadeText, vm.BinText));
            Log(string.Format("  空态标志: HasTodos={0} HasWants={1} HasBins={2} HasDoneGroups={3} 显示灌数据行={4}",
                vm.HasTodos, vm.HasWants, vm.HasBins, vm.HasDoneGroups, vm.ShowSeedRow));

            Log("  --- Todo 列表（顺序应为：未完成在前，today 优先）---");
            foreach (var t in vm.Todos)
                Log(string.Format("    [{0}] {1}  卡={2} 完成={3} 逾期={4} 来源={5} 时间={6}",
                    string.IsNullOrEmpty(t.PillText) ? "—" : t.PillText,
                    t.Title, t.PillKind ?? "—", t.IsDone, t.Overdue, t.MetaSource, t.MetaTime));

            Log("  --- Wantdo 列表 ---");
            foreach (var w in vm.Wants)
                Log(string.Format("    「{0}」 副标题={1} 首字={2} 透明度={3:0.##} 生命={4:0.#}% 提示={5}",
                    w.Title, w.Caption, w.FirstChar, w.Opacity, w.LifePercent, w.FadingHint ?? "—"));

            Log("  --- 完成列表分组 ---");
            foreach (var g in vm.DoneGroups)
            {
                Log(string.Format("    {0}（{1} 条）", g.Label, g.Count));
                foreach (var d in g.Items)
                    Log(string.Format("      {0} [{1}] {2}", d.Title, d.ChipKind, d.MetaText));
            }

            Log(string.Format("  缩略图: 有图条目 {0} 个（演示数据无图片，应为 0）",
                CountWithThumb(vm)));
            Log("=== 自检结束 ===");
        }

        static int CountWithThumb(PanelViewModel vm)
        {
            int n = 0;
            foreach (var t in vm.Todos) if (t.HasThumb) n++;
            foreach (var w in vm.Wants) if (w.HasThumb) n++;
            foreach (var b in vm.Bins) if (b.HasThumb) n++;
            return n;
        }

        static void AppMain()
        {
            AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .WithInterFont()
                .LogToTrace()
                .StartWithClassicDesktopLifetime(new string[0], ShutdownMode.OnMainWindowClose);
        }

        /// <summary>
        /// 不抓屏、不显示窗口，只构造一遍所有窗口，验证 XAML 与控件绑定。
        /// 这是 P3 阶段最重要的**免权限**验收手段：GUI 出错常常是「窗口一闪没了」，
        /// 有了它就能把「标记写错」和「运行时/权限问题」分开。
        /// </summary>
        static void RunXamlSelfTest()
        {
            var app = AppBuilder.Configure<SelfTestApp>()
                .UsePlatformDetect()
                .WithInterFont()
                .LogToTrace()
                .SetupWithoutStarting();

            Dispatcher.UIThread.Invoke(() =>
            {
                int ok = 0, fail = 0;
                void Check(string name, Action build)
                {
                    Boot.Restart();
                    try
                    {
                        build();
                        Log(string.Format("  ✓ {0}（{1}ms）", name, Boot.ElapsedMilliseconds));
                        ok++;
                    }
                    catch (Exception ex)
                    {
                        Log(string.Format("  ✗ {0}：{1} — {2}", name, ex.GetType().Name, ex.Message));
                        fail++;
                    }
                }

                var disp = new DisplayInfo { Id = 1, X = 0, Y = 0, W = 2560, H = 1440, Scale = 1, IsPrimary = true };

                Check("PanelWindow", () => { var w = new PanelWindow(); });
                Check("OverlayWindow", () => { var w = new OverlayWindow(new Screenshot
                    {
                        Width = 8, Height = 8, Bgra = new byte[8 * 8 * 4],
                    }, disp, false); });
                Check("ChooserWindow", () => { var w = new ChooserWindow(new ChooserWindow.Payload
                    {
                        App = "测试应用",
                        Title = "测试窗口标题",
                    }, disp, 100, 100, 900, 560); });
                Check("PreviewWindow", () => { var w = new PreviewWindow(); });
                Check("DetailWindow", () => { var w = new DetailWindow(new Store(DataDir), FirstItemId()); });
                Check("RecordWindow(write)", () => { var w = new RecordWindow(new Store(DataDir), FirstItemId(), "write"); });

                Log(string.Format("=== XAML 自检结果：成功 {0} / 失败 {1} ===", ok, fail));
                Environment.ExitCode = fail == 0 ? 0 : 1;
            });

            Environment.Exit(Environment.ExitCode);
        }

        /// <summary>
        /// 框选层端到端自检（需要屏幕录制权限）。
        /// 自动画一个 900x560 的选区，把窗口自身渲染成 PNG 存下来。
        /// </summary>
        static void RunOverlayTest()
        {
            var ds = Platform.GetDisplays();
            if (ds.Count == 0) { Log("!! 没有可用显示器"); Environment.Exit(1); }
            var d = ds[0];
            foreach (var c in ds) if (c.IsPrimary) d = c;

            Log("=== 框选层自检 ===");
            Log("  目标屏: " + d);
            var shot = Platform.CaptureDisplay(d);
            if (shot == null) { Log("!! 抓屏失败，无法继续（先把屏幕录制权限搞定）"); Environment.Exit(1); }
            Log(string.Format("  底图 = {0}x{1}", shot.Width, shot.Height));

            var app = AppBuilder.Configure<SelfTestApp>()
                .UsePlatformDetect()
                .WithInterFont()
                .LogToTrace()
                .SetupWithoutStarting();

            OverlayWindow overlay = null;
            Dispatcher.UIThread.Invoke(() =>
            {
                overlay = new OverlayWindow(shot, d, false);
                overlay.Show();
                Log("  框选层已显示");
            });

            // 等渲染完一帧，再设定选区并自检。
            // 直接持有 overlay 引用，不去翻 lifetime.Windows —— Show() 返回时窗口
            // 还没被登记进去，按索引取会在等待期间拿到空集合。
            DispatcherTimer.RunOnce(() =>
            {
                try
                {
                    if (overlay == null) { Log("!! 框选层没建起来"); Environment.Exit(1); }
                    int w = 900, h = 560;
                    int x = Math.Max(0, (shot.Width - w) / 2);
                    int y = Math.Max(0, (shot.Height - h) / 2);
                    overlay.SetSelectionForTest(x, y, w, h, OverlayTest);
                }
                catch (Exception ex)
                {
                    Log("!! 框选层自检失败：" + ex.GetType().Name + " " + ex.Message);
                }
                Environment.Exit(0);
            }, TimeSpan.FromMilliseconds(1200));
        }

        /// <summary>
        /// 自检用的应用对象。
        ///
        /// 刻意**继承 App** 而不是直接继承 Application：App.Initialize 里加载了
        /// Theme.axaml / Styles.axaml，如果自检绕开它，测的就不是真实启动路径 ——
        /// 曾经因此漏掉过一次「Static resource not found」。
        /// 只把「自动建主窗口」这一步让掉，好让自检自己造窗口。
        /// </summary>
        public class SelfTestApp : App
        {
            public override void OnFrameworkInitializationCompleted()
            {
                // 不建主窗口，也不调 base（base 会去建 PanelWindow）
            }
        }

        /// <summary>
        /// mac 的数据目录。用 Application Support 而不是 Documents，符合 mac 惯例，
        /// 也不会被 iCloud 同步搅乱（截图和 JSON 放进去会同步一堆大文件）。
        /// </summary>
        public static string DefaultDataDir()
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(home)) home = Environment.GetEnvironmentVariable("HOME");
            return Path.Combine(home, "Library", "Application Support", "remeber-er");
        }

        static void ParseArgs(string[] args)
        {
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
                else if (a == "--request-perms") RequestPerms = true;
                else if (a.StartsWith("--grab=")) GrabPng = a.Substring(7);
                else if (a.StartsWith("--overlay-test=")) OverlayTest = a.Substring(15);
                else if (a == "--xaml-selftest") XamlSelfTest = true;
                else if (a == "--seed") Seed = true;
                else if (a.StartsWith("--shot-panel=")) ShotPanel = a.Substring(13);
                else if (a.StartsWith("--shot-chooser=")) ShotChooser = a.Substring(15);
                else if (a.StartsWith("--shot-detail=")) ShotDetail = a.Substring(14);
                else if (a == "--dock-test") DockTest = true;
                else if (a == "--hotkey-test") HotkeyTest = true;
                else if (a.StartsWith("--theme-test=")) ThemeTest = a.Substring(13);
                else if (a.StartsWith("--tab=")) Tab = a.Substring(6);
                else if (a == "--confirm-trash") ConfirmTrash = true;
                else if (a == "--pos-test") PosTest = true;
                else if (a == "--attach-test") AttachTest = true;
                else if (a.StartsWith("--autodays=")) AutoDays = int.Parse(a.Substring(11));
                else if (a == "--multi-window-test") MultiWindowTest = true;
                else if (a.StartsWith("--demo-banner=")) DemoBanner = a.Substring(14);
                else if (a == "--manual-chooser") ManualChooser = true;
                else if (a == "--manual-add-test") ManualAddTest = true;
                else if (a == "--hotkey-recv-test") HotkeyRecvTest = true;
                else if (a == "--bench") BenchMode = true;
                else if (a.StartsWith("--shot-record=")) ShotRecord = a.Substring(14);
                else if (a == "--vm-selftest") VmSelfTest = true;
            }
        }

        /// <summary>把显示器/缩放实情写进日志 —— 移植期这是最容易出错的地方。</summary>
        static void LogDisplayDiag()
        {
            try
            {
                var ds = Platform.GetDisplays();
                Log("  显示器数 = " + ds.Count);
                foreach (var d in ds) Log("    " + d);
            }
            catch (Exception ex)
            {
                Log("  !! 显示器枚举失败：" + ex.Message);
            }

            try
            {
                var cur = Platform.GetCursorPosition();
                Log("  光标(物理) = " + cur);
            }
            catch (Exception ex)
            {
                Log("  !! 光标位置读取失败：" + ex.Message);
            }

            // 权限各自单独 try —— 某一项还没实现（P3 前）不能连累其它诊断信息，
            // 否则日志看起来像「整个平台层都坏了」，排查时会被带偏。
            TryLog("屏幕录制权限", () => Platform.HasScreenCapturePermission() ? "已授权" : "未授权");
            TryLog("辅助功能权限", () => Platform.HasAccessibilityPermission() ? "已授权" : "未授权");
            TryLog("输入监控权限", () => HasInputMonitoring() ? "已授权" : "未授权（全局热键需要它）");
        }

        /// <summary>输入监控权限。取不到就当没有（StubPlatform 上没有这个方法）。</summary>
        public static bool HasInputMonitoring()
        {
            try
            {
                var mp = Platform as MacPlatform;
                return mp != null && mp.HasInputMonitoringPermission();
            }
            catch (Exception) { return false; }
        }

        static void TryLog(string label, Func<string> f)
        {
            try { Log("  " + label + " = " + f()); }
            catch (Exception ex) { Log("  " + label + " = 未实现（" + ex.Message + "）"); }
        }

        /// <summary>在图上取几个点读 BGRA，用来判断抓到的不是纯黑/纯色。</summary>
        static string SamplePixels(Screenshot s, int n)
        {
            if (s == null || s.Bgra == null) return "(无数据)";
            var sb = new StringBuilder();
            for (int i = 0; i < n; i++)
            {
                int x = s.Width * (i + 1) / (n + 1);
                int y = s.Height * (i + 1) / (n + 1);
                int o = (y * s.Width + x) * 4;
                if (o + 3 >= s.Bgra.Length) continue;
                sb.Append(string.Format("({0},{1})=#{2:X2}{3:X2}{4:X2} ",
                    x, y, s.Bgra[o + 2], s.Bgra[o + 1], s.Bgra[o]));
            }
            return sb.ToString().Trim();
        }

        /// <summary>
        /// 正常是 GUI 程序；从终端重定向运行时标准输出仍然有效，
        /// 所以两边都写 —— 日志文件保证任何时候都拿得到（和 Windows 版一致）。
        /// </summary>
        public static void Log(string s)
        {
            try { Console.WriteLine(s); Console.Out.Flush(); } catch (Exception) { }
            try
            {
                if (LogPath != null)
                    File.AppendAllText(LogPath, s + Environment.NewLine, new UTF8Encoding(false));
            }
            catch (Exception) { }
        }
    }

    /// <summary>
    /// Avalonia 应用。WPF 版用 ShutdownMode.OnMainWindowClose 的语义在这里保持一致。
    /// </summary>
    public class App : Application
    {
        /// <summary>
        /// 主题与样式的加载开关。
        ///
        /// 为什么需要它：Application.Initialize 在 Avalonia 里**只能被调用一次**
        /// （再次 Setup 会抛 “Cannot re-initialize”）。而自检要建自己的应用实例，
        /// 于是 Initialize 会被再走一遍。用一个静态开关保证只加载一次，
        /// 同时让自检复用**同一份**加载代码 —— 否则自检测的就不是真实路径了。
        /// </summary>
        public static bool ThemeLoaded;

        public override void Initialize()
        {
            LoadTheme();
        }

        /// <summary>
        /// 加载主题与样式。刻意抽成静态方法，App 与自检共用。
        ///
        /// 刻意**不**引入 FluentTheme：原版面板是自绘的（Theme.xaml 里一套 token
        /// + 控件模板），引入 Fluent 会往自绘控件上叠一层不需要的默认样式，
        /// 而且它的默认窗口背景会干扰「面板半透明」这个核心视觉。
        /// SimpleTheme 只提供滚动条这类基础件，其余全部由我们自己的样式接管。
        /// </summary>
        public static void LoadTheme()
        {
            if (ThemeLoaded) return;
            ThemeLoaded = true;

            var app = Application.Current;
            if (app == null) return;


            // 设计 token（颜色/转换器/字体），照抄 WPF 版 native/src/Theme.xaml。
            //
            // 用 AvaloniaXamlLoader.Load(uri) 直接按 URI 载入字典，而不是
            // new ResourceInclude { Source = ... } —— 后者在 Avalonia 12 里
            // 有个坑：Source 必须通过对象初始化器赋值，用构造函数传会留下
            // 未赋值的 Source，运行时抛 “ResourceInclude.Source must be set.”。
            var themeUri = new Uri("avares://RemeberEr.Mac/Theme.axaml");
            var theme = (ResourceDictionary)AvaloniaXamlLoader.Load(themeUri);

            // 注意顺序：先把资源放进应用，再挂样式。
            // SimpleTheme 的样式在求值时就会去查这些 token，反了会解析不到。
            app.Resources.MergedDictionaries.Add(theme);

            // SimpleTheme 只提供滚动条等基础控件模板，放在我们自己的样式之前
            app.Styles.Add(new Avalonia.Themes.Simple.SimpleTheme());

            // 同样的坑，而且更深一层：StyleInclude 的 ctor(Uri) 参数是 **baseUri** 而不是
            // Source，且 Source 是 init 属性（没有无参构造，没法用对象初始化器补）。
            // 直接按 URI 载入，和上面 Theme.axaml 一样走 AvaloniaXamlLoader，最省事也最明确。
            var styles = (IStyle)AvaloniaXamlLoader.Load(
                new Uri("avares://RemeberEr.Mac/Styles.axaml"));
            app.Styles.Add(styles);
        }

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.MainWindow = new PanelWindow();
                Program.Log("  [t=" + Program.Boot.ElapsedMilliseconds + "ms] PanelWindow 已构造");
            }
            base.OnFrameworkInitializationCompleted();
        }
    }
}
