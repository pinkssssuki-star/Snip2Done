using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace RemeberEr
{
    public static class Program
    {
        public static string Root;        // 仓库根
        public static string DataDir;     // 本原型自己的数据副本
        public static string Mode = "layered";
        public static bool Bench;
        public static bool NoCapture;
        public static int BenchDelayMs = 1500;
        public static bool PrimaryOnly;
        public static string ShotPath;
        public static string DumpSnap;

        [STAThread]
        public static void Main(string[] args)
        {
            // 必须先于任何窗口创建调用。不设的话进程是 DPI 不感知的：
            // SystemParameters.WorkArea 会给物理像素(2560x1392)，而窗口坐标被系统按 96DPI 虚拟化，
            // 两者一混用窗口就被丢到屏幕外，同时整个 UI 被系统拉伸变糊。
            try { Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); } // PER_MONITOR_AWARE_V2
            catch (Exception) { }

            foreach (var a in args)
            {
                if (a.StartsWith("--root=")) Root = a.Substring(7);
                else if (a.StartsWith("--data=")) DataDir = a.Substring(7);
                else if (a.StartsWith("--mode=")) Mode = a.Substring(7);
                else if (a.StartsWith("--shot=")) ShotPath = a.Substring(7);
                else if (a == "--bench") Bench = true;
                else if (a == "--nocapture") NoCapture = true;
                else if (a.StartsWith("--benchdelay=")) BenchDelayMs = int.Parse(a.Substring(13));
                else if (a == "--primary") PrimaryOnly = true;
                else if (a.StartsWith("--dumpsnap=")) DumpSnap = a.Substring(11);
                else if (a.StartsWith("--now=")) Store.NowOverride = long.Parse(a.Substring(6));
            }

            if (Root == null) Root = AppDomain.CurrentDomain.BaseDirectory;
            if (DataDir == null) DataDir = Path.Combine(Root, ".tmp", "wpf-data");

            Console.OutputEncoding = Encoding.UTF8;
            Log("RemeberEr.Wpf 启动 mode=" + Mode + (Bench ? " bench" : ""));
            Log("  仓库根 = " + Root);
            Log("  数据目录 = " + DataDir);

            PrepareDataDir();

            // 纯数据比对模式：不起窗口，不碰 WebView2，直接把 snapshot() 落盘
            if (DumpSnap != null)
            {
                var st = new Store(DataDir);
                File.WriteAllText(DumpSnap, Json.Stringify(st.Snapshot()), new UTF8Encoding(false));
                Log("  snapshot 已写出 -> " + DumpSnap);
                return;
            }

            var app = new Application();
            var win = new PanelWindow();
            app.Run(win);
        }

        /// <summary>
        /// 把 Electron 版的真实数据复制一份到工作区里，原型只读写这份副本。
        /// 这样两个实现的 snapshot() 可以指向同一批数据做逐字段比对。
        /// </summary>
        static void PrepareDataDir()
        {
            Directory.CreateDirectory(DataDir);
            string mine = Path.Combine(DataDir, "data.json");
            if (File.Exists(mine)) { Log("  数据副本已存在，沿用"); return; }

            string src = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "remeber-er");
            string srcData = Path.Combine(src, "data.json");
            if (!File.Exists(srcData))
            {
                Log("  没找到 Electron 版数据（" + srcData + "），从空库开始");
                return;
            }

            File.Copy(srcData, mine, true);
            int n = 0;
            string srcImg = Path.Combine(src, "images");
            if (Directory.Exists(srcImg))
            {
                string dstImg = Path.Combine(DataDir, "images");
                Directory.CreateDirectory(dstImg);
                foreach (var f in Directory.GetFiles(srcImg, "*.png"))
                {
                    File.Copy(f, Path.Combine(dstImg, Path.GetFileName(f)), true);
                    n++;
                }
            }
            Log("  已从 Electron 版复制数据：data.json + " + n + " 张图");
        }

        public static void Log(string s)
        {
            Console.WriteLine(s);
            Console.Out.Flush();
        }
    }

    internal static class Native
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

        public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        public const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
        public const int DWMWCP_ROUND = 2;
        public const int DWMSBT_AUTO = 0;
        public const int DWMSBT_NONE = 1;
        public const int DWMSBT_MAINWINDOW = 2;      // Mica
        public const int DWMSBT_TRANSIENTWINDOW = 3; // Acrylic
        public const int DWMSBT_TABBEDWINDOW = 4;    // Tabbed

        [StructLayout(LayoutKind.Sequential)]
        public struct MARGINS { public int Left, Right, Top, Bottom; }

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
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder sb, int max);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint flags);
        [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);

        [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);
        [DllImport("dwmapi.dll")] public static extern int DwmExtendFrameIntoClientArea(IntPtr h, ref MARGINS m);

        public static string TitleOf(IntPtr h)
        {
            var sb = new StringBuilder(512);
            GetWindowTextW(h, sb, sb.Capacity);
            return sb.ToString();
        }
    }

    public class PanelWindow : Window
    {
        const int PANEL_W = 340, PANEL_H = 500, MARGIN = 24;
        const int HK_CAPTURE = 1, HK_TOGGLE = 2, HK_QUIT = 3;

        readonly Stopwatch _life = Stopwatch.StartNew();
        WebView2 _web;
        Store _store;
        IntPtr _hwnd;
        long _firstPingMs = -1;
        long _loadedMs = -1;
        readonly List<long> _captureMs = new List<long>();
        readonly List<long> _grabMs = new List<long>();
        List<int> _wv2PidsBefore = new List<int>();

        public PanelWindow()
        {
            _store = new Store(Program.DataDir);

            Title = "记忆面板";
            WindowStyle = WindowStyle.None;
            ShowInTaskbar = false;
            Topmost = true;
            Width = PANEL_W;
            Height = PANEL_H;

            if (Program.Mode == "layered" || Program.Mode == "layered-red")
            {
                // 和 Electron 一样的分层窗口：靠 WPF 的 AllowsTransparency 做逐像素透明
                AllowsTransparency = true;
                ResizeMode = ResizeMode.NoResize;
                // 探针：底色刷成纯红。如果 WebView2 的 DefaultBackgroundColor=Transparent 真生效，
                // 面板那层 rgba 半透明底就会泛红；否则完全看不出红色。
                Background = Program.Mode == "layered-red"
                    ? System.Windows.Media.Brushes.Red
                    : System.Windows.Media.Brushes.Transparent;
            }
            else
            {
                // 不透明窗口 + DWM 背景材质（Win11 22H2+）
                AllowsTransparency = false;
                ResizeMode = ResizeMode.CanResize;
                Background = System.Windows.Media.Brushes.Transparent;
            }

            if (Program.Mode != "native")
            {
                _web = new WebView2
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Stretch,
                    // 让 HTML 里的 rgba 底色真的能透出去
                    DefaultBackgroundColor = System.Drawing.Color.Transparent,
                };
                Content = _web;
            }

            var wa = SystemParameters.WorkArea;
            Left = wa.Right - PANEL_W - MARGIN;
            Top = wa.Bottom - PANEL_H - MARGIN;

            Loaded += OnLoaded;
            Closed += (s, e) =>
            {
                if (_hwnd != IntPtr.Zero)
                {
                    Native.UnregisterHotKey(_hwnd, HK_CAPTURE);
                    Native.UnregisterHotKey(_hwnd, HK_TOGGLE);
                    Native.UnregisterHotKey(_hwnd, HK_QUIT);
                }
                Application.Current.Shutdown();
            };
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            _hwnd = new WindowInteropHelper(this).Handle;
            var src = HwndSource.FromHwnd(_hwnd);
            src.AddHook(WndProc);
            ApplyBackdrop();
            RegisterHotKeys();
        }

        void ApplyBackdrop()
        {
            int backdrop;
            switch (Program.Mode)
            {
                case "acrylic": backdrop = Native.DWMSBT_TRANSIENTWINDOW; break;
                case "mica": backdrop = Native.DWMSBT_MAINWINDOW; break;
                case "layered": backdrop = Native.DWMSBT_NONE; break;
                default: backdrop = Native.DWMSBT_NONE; break;
            }
            if (backdrop != Native.DWMSBT_NONE)
            {
                var m = new Native.MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
                int hr1 = Native.DwmExtendFrameIntoClientArea(_hwnd, ref m);
                int bd = backdrop;
                int hr2 = Native.DwmSetWindowAttribute(_hwnd, Native.DWMWA_SYSTEMBACKDROP_TYPE, ref bd, 4);
                int corner = Native.DWMWCP_ROUND;
                int hr3 = Native.DwmSetWindowAttribute(_hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, 4);
                Program.Log(string.Format("  DWM: ExtendFrame hr={0} Backdrop({1}) hr={2} Corner hr={3}", hr1, backdrop, hr2, hr3));
            }
        }

        void RegisterHotKeys()
        {
            int mods = Native.MOD_CONTROL | Native.MOD_SHIFT | Native.MOD_NOREPEAT;
            Program.Log(string.Format("  热键 Ctrl+Shift+A={0} Ctrl+Shift+H={1} Ctrl+Shift+Q={2}",
                Native.RegisterHotKey(_hwnd, HK_CAPTURE, mods, 0x41),
                Native.RegisterHotKey(_hwnd, HK_TOGGLE, mods, 0x48),
                Native.RegisterHotKey(_hwnd, HK_QUIT, mods, 0x51)));
        }

        IntPtr WndProc(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled)
        {
            if (msg == Native.WM_HOTKEY)
            {
                int id = w.ToInt32();
                if (id == HK_CAPTURE) { DoCapture("hotkey"); handled = true; }
                else if (id == HK_TOGGLE)
                {
                    if (IsVisible) Hide(); else { Show(); Activate(); }
                    handled = true;
                }
                else if (id == HK_QUIT) { Close(); handled = true; }
            }
            return IntPtr.Zero;
        }

        // ---------------- WebView2 初始化 ----------------

        void OnLoaded(object sender, RoutedEventArgs e)
        {
            // native 档：完全不碰 WebView2，纯 WPF 画一个面板。
            // 用来量「如果 UI 用 XAML 重写」的地板：进程数 / 内存 / 启动时间。
            if (Program.Mode == "native")
            {
                Content = BuildNativePanel();
                _firstPingMs = _life.ElapsedMilliseconds;
                _loadedMs = _firstPingMs;
                Program.Log("  原生 WPF 面板就绪 @ " + _firstPingMs + "ms（无 WebView2）");
                if (Program.Bench) ScheduleBench();
                if (Program.ShotPath != null) ScheduleShot();
                return;
            }
            InitAsync();
        }

        /// <summary>
        /// 一个只用 WPF 画的假面板 —— 结构照着 panel.css 抄，只为量开销，不追求像素级还原。
        /// </summary>
        static UIElement BuildNativePanel()
        {
            var outer = new System.Windows.Controls.Border
            {
                CornerRadius = new CornerRadius(14),
                Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(214, 255, 255, 255)),
                BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(28, 0, 0, 0)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(12),
            };
            var stack = new System.Windows.Controls.StackPanel();

            var head = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
            head.Children.Add(new System.Windows.Shapes.Ellipse
            {
                Width = 7, Height = 7,
                Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(91, 108, 255)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0),
            });
            head.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = "记忆面板（原生 WPF 档）",
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            });
            stack.Children.Add(head);

            string[] rows = { "Review pricing page copy for Marc", "把课程设计的数据跑完，周四要交" };
            foreach (var r in rows)
            {
                var card = new System.Windows.Controls.Border
                {
                    CornerRadius = new CornerRadius(8),
                    Background = System.Windows.Media.Brushes.White,
                    BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(22, 0, 0, 0)),
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(8, 6, 8, 6),
                    Margin = new Thickness(0, 8, 0, 0),
                    Child = new System.Windows.Controls.TextBlock { Text = r, FontSize = 11, TextWrapping = TextWrapping.Wrap },
                };
                stack.Children.Add(card);
            }

            stack.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = "这一档没有 WebView2：\n进程 1 个，内存和启动就是原生 WPF 的地板。",
                FontSize = 10,
                Margin = new Thickness(0, 12, 0, 0),
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(120, 120, 130)),
                TextWrapping = TextWrapping.Wrap,
            });

            outer.Child = stack;
            return outer;
        }

        async void InitAsync()
        {
            try
            {
                string udf = Path.Combine(Program.Root, ".tmp", "wpf-userdata");
                Directory.CreateDirectory(udf);
                _wv2PidsBefore = WebView2Pids();

                // appimg:// 是 Electron 自定义协议，这里用 SDK 的自定义协议注册还原它。
                // 注意：CustomSchemeRegistrations 是只读属性，只能在构造函数里传进去，
                // 用无参构造的话它会是 null —— 这是 SDK 的一个坑。
                var regs = new List<CoreWebView2CustomSchemeRegistration>();
                try
                {
                    var reg = new CoreWebView2CustomSchemeRegistration("appimg")
                    {
                        HasAuthorityComponent = true,
                        TreatAsSecure = true,
                    };
                    if (reg.AllowedOrigins != null) reg.AllowedOrigins.Add("https://app.local");
                    regs.Add(reg);
                    Program.Log("  已注册自定义协议 appimg://");
                }
                catch (Exception ex)
                {
                    Program.Log("  !! 注册 appimg 失败: " + ex.GetType().Name + " " + ex.Message);
                }

                var opts = new CoreWebView2EnvironmentOptions(null, null, null, false, regs);

                var env = await CoreWebView2Environment.CreateAsync(null, udf, opts);
                await _web.EnsureCoreWebView2Async(env);

                var core = _web.CoreWebView2;
                string renderer = Path.Combine(Program.Root, "src", "renderer");
                core.SetVirtualHostNameToFolderMapping(
                    "app.local", renderer, CoreWebView2HostResourceAccessKind.Allow);

                core.AddWebResourceRequestedFilter("appimg://*", CoreWebView2WebResourceContext.All);
                core.WebResourceRequested += OnResourceRequested;
                core.WebMessageReceived += OnWebMessage;
                core.Settings.AreDefaultContextMenusEnabled = false;
                core.Settings.IsZoomControlEnabled = false;
                core.Settings.AreBrowserAcceleratorKeysEnabled = false;
                core.Settings.IsStatusBarEnabled = false;

                string shim = File.ReadAllText(Path.Combine(Program.Root, "csharp", "wpf", "shim.js"));
                await core.AddScriptToExecuteOnDocumentCreatedAsync(shim);

                core.NavigationCompleted += (s, ev) =>
                {
                    _loadedMs = _life.ElapsedMilliseconds;
                    Program.Log(string.Format("  NavigationCompleted ok={0} @{1}ms", ev.IsSuccess, _loadedMs));
                    if (ev.IsSuccess) Dispatcher.BeginInvoke(new Action(PushState));
                };

                Program.Log("  WebView2 初始化完成 @ " + _life.ElapsedMilliseconds + "ms，开始导航");
                core.Navigate("https://app.local/panel/index.html");
            }
            catch (Exception ex)
            {
                Program.Log("  !! WebView2 初始化失败: " + ex);
                Application.Current.Shutdown(2);
            }
        }

        static List<int> WebView2Pids()
        {
            var list = new List<int>();
            foreach (var p in Process.GetProcessesByName("msedgewebview2"))
            {
                try { list.Add(p.Id); } catch (Exception) { }
                p.Dispose();
            }
            return list;
        }

        /// <summary>
        /// 只认命令行里带我们自己的 user-data 目录的 msedgewebview2 —— 机器上别的应用
        /// （Edge / 小组件 / Copilot）也会起 WebView2 进程，只按进程名算会把它们算到我们头上。
        /// </summary>
        static HashSet<int> WebView2PidsTagged(string udf)
        {
            var set = new HashSet<int>();
            try
            {
                using (var searcher = new System.Management.ManagementObjectSearcher(
                    "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name='msedgewebview2.exe'"))
                {
                    foreach (System.Management.ManagementObject mo in searcher.Get())
                    {
                        string cmd = mo["CommandLine"] as string ?? "";
                        if (cmd.IndexOf(udf, StringComparison.OrdinalIgnoreCase) >= 0)
                            set.Add(Convert.ToInt32(mo["ProcessId"]));
                        mo.Dispose();
                    }
                }
            }
            catch (Exception ex) { Program.Log("  !! WMI 归属核对失败: " + ex.Message); }
            return set;
        }

        void OnResourceRequested(object sender, CoreWebView2WebResourceRequestedEventArgs e)
        {
            string uri = e.Request.Uri;
            int i = uri.IndexOf("://", StringComparison.Ordinal);
            string rest = i >= 0 ? uri.Substring(i + 3) : uri;
            int slash = rest.IndexOf('/');
            string path = slash >= 0 ? rest.Substring(slash + 1) : "";
            path = Uri.UnescapeDataString(path);
            string full = Path.Combine(_store.DataDir, path.Replace('/', Path.DirectorySeparatorChar));

            if (!File.Exists(full))
            {
                e.Response = _web.CoreWebView2.Environment.CreateWebResourceResponse(
                    null, 404, "Not Found", "Content-Type: text/plain");
                return;
            }
            var fs = File.OpenRead(full);
            e.Response = _web.CoreWebView2.Environment.CreateWebResourceResponse(
                fs, 200, "OK", "Content-Type: image/png\r\nCache-Control: no-store");
        }

        // ---------------- 消息分发 ----------------

        void OnWebMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            Dictionary<string, object> m;
            try { m = J.Obj(Json.Parse(e.WebMessageAsJson)); }
            catch (Exception) { return; }
            if (m == null) return;

            string kind = J.Str(m, "kind", "");

            if (kind == "ping")
            {
                if (_firstPingMs < 0)
                {
                    _firstPingMs = _life.ElapsedMilliseconds;
                    Program.Log(string.Format("  ★ UI 就绪 ping({0}) @ {1}ms", J.Str(m, "tag", ""), _firstPingMs));
                    if (Program.Bench) ScheduleBench();
                    if (Program.ShotPath != null) ScheduleShot();
                }
                return;
            }

            if (kind == "drag")
            {
                Native.ReleaseCapture();
                Native.SendMessage(_hwnd, Native.WM_NCLBUTTONDOWN, (IntPtr)Native.HTCAPTION, IntPtr.Zero);
                return;
            }

            if (kind == "send")
            {
                string ch = J.Str(m, "channel", "");
                if (ch == "preview:show") { /* 悬停预览窗：原型未实现 */ }
                return;
            }

            if (kind != "invoke") return;

            int id = (int)J.Num(m, "id", 0);
            string channel = J.Str(m, "channel", "");
            var args = J.Arr(J.Get(m, "args"));

            object result = null;
            string error = null;
            try { result = HandleInvoke(channel, args); }
            catch (Exception ex) { error = ex.Message; }

            var reply = new Dictionary<string, object>
            {
                { "kind", "result" },
                { "id", (double)id },
                { "ok", error == null },
            };
            if (error == null) reply["value"] = result;
            else reply["error"] = error;
            Post(reply);
        }

        object HandleInvoke(string channel, List<object> a)
        {
            switch (channel)
            {
                case "state:get":
                    return _store.Snapshot();

                case "item:touch":
                    _store.Touch(J.Str(a[0], ""));
                    PushState();
                    return true;

                case "item:bin":
                    _store.Bin(J.Str(a[0], ""));
                    PushState();
                    return true;

                case "item:restore":
                    _store.Restore(J.Str(a[0], ""));
                    PushState();
                    return true;

                case "item:file":
                    _store.File_(J.Str(a[0], ""));
                    PushState();
                    return true;

                case "item:remove":
                    _store.Remove(J.Str(a[0], ""));
                    PushState();
                    return true;

                case "item:update":
                {
                    var p = J.Obj(a[0]);
                    _store.Update(J.Str(p, "id", ""), J.Obj(J.Get(p, "patch")));
                    PushState();
                    return true;
                }

                case "item:done":
                {
                    // payload 可能是 id 字符串，也可能是 { id, reflection, file }
                    if (a != null && a.Count > 0 && a[0] is string)
                    {
                        _store.Done((string)a[0], false, null, true);
                    }
                    else
                    {
                        var p = J.Obj(a[0]);
                        bool hasRefl = !J.IsNull(J.Get(p, "reflection"));
                        bool file = J.Bool(p, "file", true);
                        _store.Done(J.Str(p, "id", ""), hasRefl, J.Str(p, "reflection", ""), file);
                    }
                    PushState();
                    return true;
                }

                case "item:addManual":
                {
                    var p = J.Obj(a[0]);
                    _store.Add(J.Str(p, "room", "todo"), J.Str(p, "title", ""), "", null, null);
                    PushState();
                    return true;
                }

                case "settings:set":
                {
                    var p = J.Obj(a[0]);
                    _store.SetSetting(J.Str(p, "key", ""), J.Get(p, "value"));
                    PushState();
                    return _store.Settings;
                }

                case "capture:start":
                    Dispatcher.BeginInvoke(new Action(() => DoCapture("ui")));
                    return true;

                case "panel:resize":
                {
                    var p = J.Obj(a[0]);
                    double w = J.Num(p, "width", PANEL_W);
                    double h = J.Num(p, "height", PANEL_H);
                    Width = w; Height = h;
                    Left = SystemParameters.WorkArea.Right - w - MARGIN;
                    Top = SystemParameters.WorkArea.Bottom - h - MARGIN;
                    return true;
                }

                case "panel:hide":
                    Hide();
                    return true;

                case "panel:quit":
                    Dispatcher.BeginInvoke(new Action(Close));
                    return true;

                case "demo:seed":
                    _store.SeedDemo();
                    PushState();
                    return true;

                case "record:open":
                case "item:openDetail":
                    // 详情/小结窗在原型里没搬过来 —— 面板与缩略图渲染才是这次要比的东西
                    Program.Log("  [未实现] " + channel);
                    return true;

                default:
                    Program.Log("  [未知通道] " + channel);
                    return true;
            }
        }

        void Post(object payload)
        {
            if (_web == null || _web.CoreWebView2 == null) return; // native 档没有 WebView2
            _web.CoreWebView2.PostWebMessageAsJson(Json.Stringify(payload));
        }

        void PushState()
        {
            Post(new Dictionary<string, object>
            {
                { "kind", "state" },
                { "value", _store.Snapshot() },
            });
        }

        // ---------------- 截图 ----------------

        void DoCapture(string trigger)
        {
            var sw = Stopwatch.StartNew();
            bool wasVisible = IsVisible;
            if (wasVisible) Hide();
            System.Threading.Thread.Sleep(140);   // 等 DWM 真的把面板从屏幕撤掉
            long hideMs = sw.ElapsedMilliseconds;

            long grabMs;
            using (Bitmap bmp = GrabScreen(out grabMs))
            {
                long pngMs;
                byte[] png = EncodePng(bmp, out pngMs);

                string id = Guid.NewGuid().ToString();
                string rel = _store.SaveImage(id, png);
                string title = ForegroundTitle();
                _store.Add("want", title, "屏幕", rel, null, id);

                _captureMs.Add(sw.ElapsedMilliseconds);
                _grabMs.Add(grabMs);
                Program.Log(string.Format(
                    "  截图[{0}] hide={1}ms grab={2}ms png={3}ms({4}KB) 共={5}ms 前台=\"{6}\"",
                    trigger, hideMs, grabMs, pngMs, png.Length / 1024, sw.ElapsedMilliseconds, title));
            }

            if (wasVisible) { Show(); Activate(); }
            PushState();
        }

        static Bitmap GrabScreen(out long grabMs)
        {
            int x, y, w, h;
            if (Program.PrimaryOnly)
            {
                // 只抓主屏，和之前 Electron 那组 2560x1440 的数据直接可比
                x = 0; y = 0;
                w = Native.GetSystemMetrics(0); // SM_CXSCREEN
                h = Native.GetSystemMetrics(1); // SM_CYSCREEN
            }
            else
            {
                x = Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN);
                y = Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN);
                w = Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN);
                h = Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN);
            }

            IntPtr screenDc = Native.GetDC(IntPtr.Zero);
            IntPtr memDc = Native.CreateCompatibleDC(screenDc);
            IntPtr hBmp = Native.CreateCompatibleBitmap(screenDc, w, h);
            IntPtr old = Native.SelectObject(memDc, hBmp);

            var sw = Stopwatch.StartNew();
            // CAPTUREBLT：不带它的话分层窗口（AllowsTransparency=true 的那一档）截不进来
            Native.BitBlt(memDc, 0, 0, w, h, screenDc, x, y, Native.SRCCOPY | Native.CAPTUREBLT);
            grabMs = sw.ElapsedMilliseconds;

            var bmp = Image.FromHbitmap(hBmp);
            Native.SelectObject(memDc, old);
            Native.DeleteObject(hBmp);
            Native.DeleteDC(memDc);
            Native.ReleaseDC(IntPtr.Zero, screenDc);
            return bmp;
        }

        static byte[] EncodePng(Bitmap bmp, out long ms)
        {
            var sw = Stopwatch.StartNew();
            using (var ms2 = new MemoryStream())
            {
                bmp.Save(ms2, ImageFormat.Png);
                ms = sw.ElapsedMilliseconds;
                return ms2.ToArray();
            }
        }

        static string ForegroundTitle()
        {
            IntPtr h = Native.GetForegroundWindow();
            // 我们自己的窗口不算
            uint pid;
            Native.GetWindowThreadProcessId(h, out pid);
            if (pid == (uint)Process.GetCurrentProcess().Id)
            {
                Native.POINT p;
                Native.GetCursorPos(out p);
                h = Native.WindowFromPoint(p);
                h = Native.GetAncestor(h, 2 /* GA_ROOT */);
            }
            string t = Native.TitleOf(h);
            return string.IsNullOrEmpty(t) ? "（未识别窗口）" : t;
        }

        // ---------------- 基准测试 / 截图 ----------------

        void ScheduleBench()
        {
            var t = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(Program.BenchDelayMs),
            };
            t.Tick += (s, e) =>
            {
                t.Stop();
                RunBench();
            };
            t.Start();
        }

        void RunBench()
        {
            // 跑 4 次截图取中位数前的热身
            if (!Program.NoCapture) { for (int i = 0; i < 4; i++) DoCapture("bench"); }

            var me = Process.GetCurrentProcess();
            me.Refresh();
            var after = WebView2Pids();
            var udf = Path.Combine(Program.Root, ".tmp", "wpf-userdata");
            var tagged = WebView2PidsTagged(udf);
            var mine = new List<int>();
            var dropped = new List<int>();
            foreach (int pid in after)
            {
                if (!_wv2PidsBefore.Contains(pid))
                {
                    if (tagged.Contains(pid)) mine.Add(pid);
                    else dropped.Add(pid);
                }
            }
            Program.Log(string.Format("  WebView2 归属核对：新增 {0} 个，其中确认属于本进程 {1} 个，剔除 {2} 个（别的应用）",
                after.Count - _wv2PidsBefore.Count, mine.Count, dropped.Count));

            long wvWs = 0, wvPriv = 0;
            var detail = new List<object>();
            foreach (int pid in mine)
            {
                try
                {
                    var p = Process.GetProcessById(pid);
                    p.Refresh();
                    wvWs += p.WorkingSet64;
                    wvPriv += p.PrivateMemorySize64;
                    detail.Add(new Dictionary<string, object>
                    {
                        { "pid", (double)pid },
                        { "wsMB", Math.Round(p.WorkingSet64 / 1048576.0, 1) },
                        { "privMB", Math.Round(p.PrivateMemorySize64 / 1048576.0, 1) },
                    });
                    p.Dispose();
                }
                catch (Exception) { }
            }

            long exeBytes = 0;
            int exeCount = 0;
            foreach (var f in Directory.GetFiles(AppDomain.CurrentDomain.BaseDirectory))
            {
                exeBytes += new FileInfo(f).Length;
                exeCount++;
            }

            var result = new Dictionary<string, object>
            {
                { "mode", Program.Mode },
                { "processCount", (double)(1 + mine.Count) },
                { "selfWsMB", Math.Round(me.WorkingSet64 / 1048576.0, 1) },
                { "selfPrivMB", Math.Round(me.PrivateMemorySize64 / 1048576.0, 1) },
                { "wvCount", (double)mine.Count },
                { "wvWsMB", Math.Round(wvWs / 1048576.0, 1) },
                { "wvPrivMB", Math.Round(wvPriv / 1048576.0, 1) },
                { "totalWsMB", Math.Round((me.WorkingSet64 + wvWs) / 1048576.0, 1) },
                { "totalPrivMB", Math.Round((me.PrivateMemorySize64 + wvPriv) / 1048576.0, 1) },
                { "threads", (double)me.Threads.Count },
                { "firstPingMs", (double)_firstPingMs },
                { "navigationMs", (double)_loadedMs },
                { "exeBytes", (double)exeBytes },
                { "exeFileCount", (double)exeCount },
                { "captureCount", (double)_captureMs.Count },
                { "captureMs", ToDoubles(_captureMs) },
                { "grabMs", ToDoubles(_grabMs) },
                { "wvProcs", detail },
                { "uiaVersion", "" },
            };

            string outFile = Path.Combine(Program.Root, ".tmp",
                "wpf-bench-" + Program.Mode + (Program.NoCapture ? "-nocap" : "") + ".json");
            Directory.CreateDirectory(Path.GetDirectoryName(outFile));
            File.WriteAllText(outFile, Json.Stringify(result), new UTF8Encoding(false));

            Program.Log("");
            Program.Log("=== 基准测试结果 (mode=" + Program.Mode + ") ===");
            Program.Log("  进程数        : " + result["processCount"] + "  (本体 1 + WebView2 " + mine.Count + ")");
            Program.Log("  总工作集      : " + result["totalWsMB"] + " MB   (本体 " + result["selfWsMB"] + " + WebView2 " + result["wvWsMB"] + ")");
            Program.Log("  总私有内存    : " + result["totalPrivMB"] + " MB");
            Program.Log("  首次 UI ping  : " + _firstPingMs + " ms");
            Program.Log("  导航完成      : " + _loadedMs + " ms");
            Program.Log("  分发体积      : " + (exeBytes / 1048576.0).ToString("0.00") + " MB / " + exeCount + " 个文件");
            Program.Log("  截图 grab     : " + string.Join(", ", ToDoubles(_grabMs).ConvertAll(x => x.ToString()).ToArray()) + " ms");
            Program.Log("  截图 全程     : " + string.Join(", ", ToDoubles(_captureMs).ConvertAll(x => x.ToString()).ToArray()) + " ms");
            Program.Log("  写出         : " + outFile);
            Program.Log("");

            Close();
        }

        void ScheduleShot()
        {
            var t = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(2000),
            };
            t.Tick += (s, e) =>
            {
                t.Stop();
                long ms;
                using (Bitmap bmp = GrabScreen(out ms))
                {
                    // 用窗口的真实物理矩形来裁，不要用 SystemParameters.WorkArea —— 那是 DIP，
                    // 150% 缩放下直接当物理像素用会裁到屏幕正中间去。
                    Native.RECT r;
                    Native.GetWindowRect(_hwnd, out r);
                    Program.Log(string.Format("  窗口物理矩形 = ({0},{1})-({2},{3})  尺寸 {4}x{5}  DIP尺寸 {6}x{7}  可见={8}",
                        r.Left, r.Top, r.Right, r.Bottom, r.Right - r.Left, r.Bottom - r.Top,
                        Width, Height, IsVisible));
                    Program.Log(string.Format("  屏幕位图 = {0}x{1}  虚拟屏原点 = ({2},{3})",
                        bmp.Width, bmp.Height,
                        Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN),
                        Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN)));

                    // 整屏也存一份，方便看窗口到底落在哪
                    bmp.Save(Program.ShotPath + ".full.png", ImageFormat.Png);

                    // 位图的 (0,0) 是虚拟屏原点，不是 (0,0) —— 双屏时原点是负数，必须减掉
                    int vx = Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN);
                    int vy = Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN);
                    const int PAD = 80;
                    var rect = new Rectangle(r.Left - PAD - vx, r.Top - PAD - vy,
                        (r.Right - r.Left) + PAD * 2, (r.Bottom - r.Top) + PAD * 2);
                    rect = Rectangle.Intersect(rect, new Rectangle(0, 0, bmp.Width, bmp.Height));

                    using (var crop = new Bitmap(rect.Width, rect.Height))
                    {
                        using (var g = Graphics.FromImage(crop))
                            g.DrawImage(bmp, new Rectangle(0, 0, rect.Width, rect.Height), rect, GraphicsUnit.Pixel);
                        crop.Save(Program.ShotPath, ImageFormat.Png);
                    }
                }
                Program.Log("  已截图 -> " + Program.ShotPath);
                Close();
            };
            t.Start();
        }

        static List<object> ToDoubles(List<long> src)
        {
            var l = new List<object>();
            foreach (long v in src) l.Add((double)v);
            return l;
        }
    }
}
