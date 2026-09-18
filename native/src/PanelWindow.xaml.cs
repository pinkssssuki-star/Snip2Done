using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
// System.Drawing 只为拿 Bitmap；Point 一律按 WPF 的算，避免和 System.Drawing.Point 撞名
using Point = System.Windows.Point;

namespace RemeberEr
{
    public partial class PanelWindow : Window
    {
        const int PANEL_W = 340, PANEL_H = 500, MARGIN = 24;
        const int COLLAPSED_W = 190, COLLAPSED_H = 44;
        const int HK_CAPTURE = 1, HK_TOGGLE = 2, HK_QUIT = 3;
        const int HOVER_DELAY = 550;   // 停够这么久才弹预览，和 panel.js 一致

        readonly Stopwatch _life = Stopwatch.StartNew();
        readonly Store _store;
        readonly PanelViewModel _vm;

        IntPtr _hwnd;
        PreviewWindow _preview;
        // 详情窗 / 记录窗都可以同时开好几扇，所以用列表而不是单个字段
        readonly List<DetailWindow> _details = new List<DetailWindow>();
        readonly List<RecordWindow> _records = new List<RecordWindow>();
        /// <summary>不为 null 时，这次截图是「补图」，结果挂到这条上而不是弹归类窗。</summary>
        string _attachToId;
        ChooserWindow _chooser;
        DispatcherTimer _hoverTimer;
        /// <summary>演示加速的节拍器（每秒一次，只在演示中真正干活）。</summary>
        DispatcherTimer _demoTick;
        string _hoverId;
        bool _collapsed;
        bool _closing;
        bool _inCapture;
        bool _captureHadPanel;

        // ---- 贴边隐藏 ----
        const double SLIVER_DIP = 7;       // 藏起来以后还露在外面的宽度
        const double DOCK_SLOP = 0.34;     // 拖出边界超过窗口这个比例，就认为想贴边
        const double DOCK_ANIM_MS = 200;   // 滑出/滑回的时长
        const double DOCK_HIDE_DELAY = 160;// 移开之后等一下再收，免得在边缘来回抖
        bool _docked;
        string _dockEdge;                  // left / right / top / bottom
        int _dockParkX, _dockParkY;        // 藏起来时的物理坐标
        int _dockOutX, _dockOutY;          // 弹出来时的物理坐标
        bool _dockRevealed;
        Stopwatch _dockClock;
        EventHandler _dockRender;
        int _dockFromX, _dockFromY, _dockToX, _dockToY;
        DispatcherTimer _dockHideTimer;

        long _firstPaintMs = -1;
        readonly List<long> _captureMs = new List<long>();
        readonly List<long> _grabMs = new List<long>();

        public PanelWindow()
        {
            InitializeComponent();
            Program.Log("  [t=" + Program.Boot.ElapsedMilliseconds + "ms]   · InitializeComponent(XAML)");

            _store = new Store(Program.DataDir);
            _vm = new PanelViewModel(_store);
            Program.Log("  [t=" + Program.Boot.ElapsedMilliseconds + "ms]   · Store + ViewModel");
            _vm.SnipRequested += () => Dispatcher.BeginInvoke(new Action(() => DoCapture("ui")));
            _vm.QuitRequested += Close;
            _vm.CollapseRequested += ToggleCollapse;
            _vm.QuickFocusRequested += () => Dispatcher.BeginInvoke(new Action(() => Keyboard.Focus(QuickBox)));
            _vm.OpenDetailRequested += OpenDetail;
            _vm.OpenRecordRequested += id => OpenRecord(id, "view");
            DataContext = _vm;

            _vm.Refresh();   // 先灌一次数据，再让窗口显示，避免闪一下空态
            Program.Log("  [t=" + Program.Boot.ElapsedMilliseconds + "ms]   · Refresh()");

            // 置顶开关
            Topmost = _vm.AlwaysOnTop;
            _vm.TopmostChanged += v =>
            {
                Topmost = v;
                // 关掉置顶时把 Z 序压下去，否则 Topmost=false 之后它可能还赖在最上面。
                // SWP_NOMOVE 不能省 —— 只写 SWP_NOSIZE 的话，传进去的 0,0 会把面板
                // 直接搬到屏幕左上角（这就是「点置顶会跳到奇怪位置」的根因）。
                if (_hwnd != IntPtr.Zero)
                    Interop.SetWindowPos(_hwnd, v ? Interop.HWND_TOPMOST : Interop.HWND_NOTOPMOST,
                        0, 0, 0, 0,
                        Interop.SWP_NOSIZE | Interop.SWP_NOMOVE | Interop.SWP_NOACTIVATE);
            };

            // 贴边隐藏：鼠标碰到那条窄边就滑出来，移开再滑回去
            MouseEnter += (s, e) => { if (_docked) RevealDock(true); };
            MouseLeave += (s, e) => { if (_docked) RevealDock(false); };

            // 演示加速的节拍器 —— 每秒重算一次，磁贴才会在眼前真的褪色。
            // 以前没有任何周期性刷新，所以那个 ⚡ 打开之后画面上什么都不动。
            // 只在演示中才跑，平时不占 CPU。
            _demoTick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _demoTick.Tick += (s, e) => { _vm.TickDemo(); };
            _demoTick.Start();

            if (Program.StartView != null) _vm.View = Program.StartView;   // 截图用的视图开关

            var wa = SystemParameters.WorkArea;
            Program.Log("  [t=" + Program.Boot.ElapsedMilliseconds + "ms]   · WorkArea");
            Left = wa.Right - PANEL_W - MARGIN;
            Top = wa.Bottom - PANEL_H - MARGIN;

            ContentRendered += OnContentRendered;
            Closed += OnClosed;
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            _hwnd = new WindowInteropHelper(this).Handle;
            Interop.HideFromAltTab(_hwnd);
            HwndSource.FromHwnd(_hwnd).AddHook(WndProc);
            Program.Log("  [t=" + Program.Boot.ElapsedMilliseconds + "ms] 窗口句柄就绪");

            int mods = Interop.MOD_CONTROL | Interop.MOD_SHIFT | Interop.MOD_NOREPEAT;
            Program.Log(string.Format("  热键 Ctrl+Shift+A={0} Ctrl+Shift+H={1} Ctrl+Shift+Q={2}",
                Interop.RegisterHotKey(_hwnd, HK_CAPTURE, mods, 0x41),
                Interop.RegisterHotKey(_hwnd, HK_TOGGLE, mods, 0x48),
                Interop.RegisterHotKey(_hwnd, HK_QUIT, mods, 0x51)));
        }

        void OnContentRendered(object sender, EventArgs e)
        {
            if (_firstPaintMs >= 0) return;
            _firstPaintMs = _life.ElapsedMilliseconds;
            Program.Log("  ★ 面板首帧渲染完成 @ " + _firstPaintMs + "ms（无 WebView2）");

            if (Program.Bench) ScheduleBench();
            if (Program.ShotPath != null && Program.Flow == null) ScheduleShot();
            if (Program.Flow == "demo") ScheduleFlowDemo();
            else if (Program.Flow == "detail") ScheduleFlowDetail();
            else if (Program.Flow == "recordview") ScheduleFlowRecord("view");
            else if (Program.Flow == "recordwrite") ScheduleFlowRecord("write");
            else if (Program.Flow == "dissolve") ScheduleFlowDissolve();
            else if (Program.Flow == "synthdrag") ScheduleFlowSynthDrag();
            else if (Program.Flow == "ui") ScheduleFlowUi();
            else if (Program.Flow == "trash") ScheduleFlowTrash();
            else if (Program.Flow == "pos") ScheduleFlowPos();
            else if (Program.Flow == "accel") ScheduleFlowAccel();
            else if (Program.Flow == "multi") ScheduleFlowMulti();
            else if (Program.Flow == "add") ScheduleFlowAdd();
            else if (Program.Flow == "theme") ScheduleFlowTheme();
            else if (Program.Flow == "manual") ScheduleFlowManual();
        }

        /// <summary>
        /// 验收「手动记一笔」：点 + 之后应该弹出**选时间**的面板（不是直接落库），
        /// 而且那个面板里没有截图、没有标题框，第三个按钮叫「无期限」。
        /// </summary>
        void ScheduleFlowManual()
        {
            var steps = new List<Tuple<int, Action>>();
            int before = 0;

            steps.Add(Tuple.Create<int, Action>(600, () =>
            {
                before = _store.Items.Count;
                _vm.QuickOpen = true;
                _vm.QuickRoom = "todo";
                _vm.QuickText = "周五前把合同发出去";
                UpdateLayout();
                Shot("manual-quick");
                Program.Log("  记一笔前条目数 = " + before);

                StartManualAdd();
                Program.Log("  点了 + 之后：面板出现了吗 = " + (_chooser != null && _chooser.IsVisible)
                    + "，条目数还是 " + _store.Items.Count + "（应等于 " + before + "，还没落库）");
            }));

            steps.Add(Tuple.Create<int, Action>(800, () =>
            {
                if (_chooser != null && _chooser.IsVisible)
                {
                    ShotWindow(_chooser, "manual-when");
                }
                else { Program.Log("  !! 时间面板没出现"); }
                if (_chooser != null && _chooser.IsVisible)
                {
                    _chooser.AutoPickWithDays("soon", 5);
                    Program.Log("  在时间面板里选：Soon + 5 天");
                }
            }));

            steps.Add(Tuple.Create<int, Action>(600, () =>
            {
                Program.Log("  选完之后条目数 = " + _store.Items.Count + "（应等于 " + (before + 1) + "）");
                foreach (var o in _store.Items)
                {
                    var it = J.Obj(o);
                    if (J.Str(it, "title", "") != "周五前把合同发出去") continue;
                    Program.Log(string.Format("  新条目：room={0} dueBucket={1} dueDays={2} 距现在={3:0.0} 小时",
                        J.Str(it, "room", ""), J.Str(it, "dueBucket", ""), J.Num(it, "dueDays", 0),
                        (J.Num(it, "dueAt", 0) - Store.Now()) / 3600000.0));
                }
                Shot("manual-after");
                Program.Log("  手动记一笔流程结束");
                Close();
            }));
            RunSteps(steps, 0);
        }

        /// <summary>
        /// 验收背景颜色 + 设置页不透明底。
        /// 关键是把透明度拉到 30%（最透）—— 那正是原来灰字糊在桌面上的极端情况。
        /// </summary>
        void ScheduleFlowTheme()
        {
            var steps = new List<Tuple<int, Action>>();
            steps.Add(Tuple.Create<int, Action>(600, () =>
            {
                _vm.View = "set";
                _vm.OpacityPercent = 30;
                var b = Application.Current.Resources["Tx2"] as SolidColorBrush;
                Program.Log("  透明度拉到 " + _vm.OpacityText
                    + "，Tx2 画笔 IsFrozen=" + (b == null ? "null" : b.IsFrozen.ToString()));
                Shot("theme-white");
            }));

            foreach (var key in new[] { "gray", "black" })
            {
                string k = key;
                steps.Add(Tuple.Create<int, Action>(600, () =>
                {
                    _vm.PanelTheme = k;
                    var tb = Application.Current.Resources["Tx2"] as SolidColorBrush;
                    var sb = Application.Current.Resources["SurfaceBg"] as SolidColorBrush;
                    Program.Log(string.Format("  切到「{0}」：Tx2 实际色 = {1}，面板底色 = {2}，SurfaceBg = {3}",
                        Palette.Current.Name,
                        tb == null ? "?" : tb.Color.ToString(),
                        Palette.Current.Panel.ToString(),
                        sb == null ? "?" : sb.Color.ToString()));
                    Shot("theme-" + k);
                }));
            }

            steps.Add(Tuple.Create<int, Action>(600, () =>
            {
                _vm.PanelTheme = "white";
                Program.Log("  切回「白」，三个选项的选中状态 = " +
                    _vm.ThemeOptions[0].Name + "=" + _vm.ThemeOptions[0].On + " / " +
                    _vm.ThemeOptions[1].Name + "=" + _vm.ThemeOptions[1].On + " / " +
                    _vm.ThemeOptions[2].Name + "=" + _vm.ThemeOptions[2].On);
                Program.Log("  主题流程结束");
                Close();
            }));
            RunSteps(steps, 0);
        }

        /// <summary>
        /// 验收「两三天 / 手动加一笔 / 补截图」三条。
        /// 补截图借 --autoregion 让框选层自己画一个框，好把整条链路走完。
        /// </summary>
        void ScheduleFlowAdd()
        {
            var steps = new List<Tuple<int, Action>>();
            string target = null;

            steps.Add(Tuple.Create<int, Action>(600, () =>
            {
                long now = Store.Now();
                var a = _store.Add("todo", "测试-直接点两三天", "", null, "soon");
                var b = _store.Add("todo", "测试-写5天", "", null, "soon", null, 5);
                var c = _store.Add("todo", "测试-Today", "", null, "today");
                Program.Log(string.Format("  两三天(没写) -> 距现在 {0:0.0} 小时（应落在 48~72）",
                    (J.Num(a, "dueAt", 0) - now) / 3600000.0));
                Program.Log(string.Format("  两三天(写5天) -> 距现在 {0:0.0} 小时（应为 120）",
                    (J.Num(b, "dueAt", 0) - now) / 3600000.0));
                Program.Log(string.Format("  Today        -> 距现在 {0:0.0} 小时",
                    (J.Num(c, "dueAt", 0) - now) / 3600000.0));
                _vm.Refresh();
                Program.Log("  标签：写5天那条应该显示 \"" +
                    (J.Num(b, "dueDays", 0) > 0 ? ((int)J.Num(b, "dueDays", 0)) + " 天" : "两三天") + "\"");
                Shot("add-main");
            }));

            steps.Add(Tuple.Create<int, Action>(400, () =>
            {
                _vm.QuickOpen = true;
                _vm.QuickText = "手动记的一笔";
                UpdateLayout();
                Program.Log(string.Format(
                    "  【量尺寸】窗口={0:0}x{1:0}  QuickBar 实际={2:0} 期望={3:0}x{4:0}  Body 实际={5:0}  底栏={6:0}",
                    ActualWidth, ActualHeight,
                    QuickBar.ActualHeight, QuickBar.DesiredSize.Width, QuickBar.DesiredSize.Height,
                    Body.ActualHeight, FooterBar.ActualHeight));
                var pt = QuickTodoBtn.TranslatePoint(new System.Windows.Point(0, 0), this);
                Program.Log(string.Format(
                    "  【量尺寸】Todo 按钮 实际高={0:0} 期望高={1:0}  顶部在窗口 y={2:0}，底部在 y={3:0}（窗口高 {4:0}）",
                    QuickTodoBtn.ActualHeight, QuickTodoBtn.DesiredSize.Height,
                    pt.Y, pt.Y + QuickTodoBtn.ActualHeight, ActualHeight));
                Shot("add-quick");   // 先看清「记一笔」这一块有没有被切
                Program.Log("  加之前 QuickOpen=" + _vm.QuickOpen);
                _vm.AddManualCmd.Execute(null);
                Program.Log(string.Format("  加之后 QuickOpen={0}（应为 False）· QuickText=\"{1}\"（应清空）",
                    _vm.QuickOpen, _vm.QuickText));
            }));

            steps.Add(Tuple.Create<int, Action>(400, () =>
            {
                foreach (var o in _store.Items)
                {
                    var it = J.Obj(o);
                    if (J.IsNull(J.Get(it, "image"))) { target = J.Str(it, "id", ""); break; }
                }
                if (target == null) { Program.Log("  !! 没有「无图」条目可用来验补截图"); return; }
                Program.Log("  补截图目标 " + target.Substring(0, 8) + " 当前 image=空");
                OpenDetail(target);
                StartCaptureForItem(target);
            }));

            steps.Add(Tuple.Create<int, Action>(3000, () =>
            {
                var it = target == null ? null : _store.Get(target);
                string img = it == null ? "-" : (J.Str(it, "image", null) ?? "(还是空)");
                Program.Log("  补截图之后 image = " + img + "（不该是「还是空」）");
                Shot("add-attached");
            }));

            steps.Add(Tuple.Create<int, Action>(500, () =>
            {
                Program.Log("  增加条目流程结束");
                Close();
            }));
            RunSteps(steps, 0);
        }

        string RectOf(Window w)
        {
            Interop.RECT r;
            Interop.GetWindowRect(new WindowInteropHelper(w).Handle, out r);
            return string.Format("({0},{1}) {2}x{3}", r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
        }

        /// <summary>验收：详情窗能同时开好几扇、错开摆放、同一条不重复开。</summary>
        void ScheduleFlowMulti()
        {
            var ids = new List<string>();
            var steps = new List<Tuple<int, Action>>();

            steps.Add(Tuple.Create<int, Action>(700, () =>
            {
                foreach (var o in _store.Items)
                {
                    var it = J.Obj(o);
                    if (J.IsNull(J.Get(it, "image"))) continue;
                    ids.Add(J.Str(it, "id", ""));
                    if (ids.Count >= 3) break;
                }
                Program.Log("  挑到 " + ids.Count + " 条有图的条目，依次打开");
                foreach (var id in ids) OpenDetail(id);
                Program.Log("  连开 " + ids.Count + " 扇 -> 实际 " + _details.Count + " 扇（应为 " + ids.Count + "）");
                foreach (var w in _details) Program.Log("     窗口 " + w.ItemId.Substring(0, 8) + " @ " + RectOf(w));
            }));

            steps.Add(Tuple.Create<int, Action>(900, () =>
            {
                if (ids.Count > 0)
                {
                    OpenDetail(ids[0]);
                    Program.Log("  重复点第 1 条 -> 实际 " + _details.Count + " 扇（应保持不变）");
                }
                Capture.SaveFullScreen(Path.Combine(Program.Root, ".guide", "native-multi.png"));
                Program.Log("  全屏截图 -> .guide\\native-multi.png");
            }));

            steps.Add(Tuple.Create<int, Action>(600, () =>
            {
                Program.Log("  多窗流程结束");
                Close();
            }));
            RunSteps(steps, 0);
        }

        void DumpWants(string tag)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var o in _store.Items)
            {
                var it = J.Obj(o);
                if (J.Str(it, "room", "") != "want") continue;
                string id = J.Str(it, "id", "");
                if (id.Length > 8) id = id.Substring(0, 8);
                sb.AppendLine(string.Format("{0}  {1,-7} last={2:0}  binCount={3}  binnedAt={4}  touches={5}",
                    id, J.Str(it, "state", ""), J.Num(it, "lastTouchedAt", 0),
                    J.Num(it, "binCount", 0), J.Num(it, "binnedAt", 0), J.Num(it, "touchCount", 0)));
            }
            File.WriteAllText(Path.Combine(Program.Root, ".guide", "wants-" + tag + ".txt"),
                sb.ToString(), new System.Text.UTF8Encoding(false));
            Program.Log("  want 快照 -> .guide\\wants-" + tag + ".txt");
        }

        /// <summary>
        /// 验收演示加速：连跑两轮，确认
        ///   1. 第一轮会把所有 Wantdo 的生命复原，第二轮不会
        ///   2. 每轮跑完数据都**原样还原**（状态、binCount、binnedAt 一个都不许变）
        ///   3. 标语按第一次 / 以后分两种
        /// </summary>
        void ScheduleFlowAccel()
        {
            var steps = new List<Tuple<int, Action>>();
            steps.Add(Tuple.Create<int, Action>(600, () =>
            {
                Program.Log(string.Format("  【原样】Wantdo {0} 条，纸篓 {1} 条", _vm.NWant, _vm.NBin));
                DumpWants("原样");
                Shot("accel-00");
                _vm.StartDemo();
            }));

            // 第一轮：边跑边记，确认是「慢慢褪色」而不是一次性清空
            int last = 0;
            for (int sec = 10; sec <= 70; sec += 10)
            {
                int delay = (sec - last) * 1000; last = sec; int at = sec;
                steps.Add(Tuple.Create<int, Action>(delay, () =>
                {
                    double minOp = 1.0;
                    foreach (var w in _vm.Wants) if (w.Opacity < minOp) minOp = w.Opacity;
                    Program.Log(string.Format("    第1轮 t={0,2}s  念想 {1} 条（最淡 {2:0.00}）· 纸篓 {3} 条 · 完成={4}",
                        at, _vm.NWant, minOp, _vm.NBin, _vm.DemoFinished));
                    if (at == 20) Shot("accel-20");
                    if (at == 40) Shot("accel-40");
                }));
            }
            steps.Add(Tuple.Create<int, Action>(500, () =>
            {
                Program.Log("  【第1轮结束】标语 = " + _vm.DemoBannerText.Replace("\n", " / "));
                DumpWants("第1轮后");
                Shot("accel-done");
                _vm.DismissDemo();
                _vm.StartDemo();
            }));

            // 第二轮
            last = 0;
            for (int sec = 10; sec <= 70; sec += 10)
            {
                int delay = (sec - last) * 1000; last = sec; int at = sec;
                steps.Add(Tuple.Create<int, Action>(delay, () =>
                {
                    Program.Log(string.Format("    第2轮 t={0,2}s  念想 {1} 条 · 纸篓 {2} 条 · 完成={3}",
                        at, _vm.NWant, _vm.NBin, _vm.DemoFinished));
                }));
            }
            steps.Add(Tuple.Create<int, Action>(500, () =>
            {
                Program.Log("  【第2轮结束】标语 = " + _vm.DemoBannerText.Replace("\n", " / "));
                DumpWants("第2轮后");
                Program.Log("  演示加速流程结束");
                Close();
            }));
            RunSteps(steps, 0);
        }

        string Pos()
        {
            Interop.RECT r;
            Interop.GetWindowRect(_hwnd, out r);
            return string.Format("物理 ({0},{1}) {2}x{3}", r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
        }

        /// <summary>
        /// 验收「点置顶 / 点折叠会不会把面板挪走」。
        /// 先把它摆到一个既不是右下角也不是原点的地方 —— 一动就能看出来。
        /// </summary>
        void ScheduleFlowPos()
        {
            var steps = new List<Tuple<int, Action>>();
            steps.Add(Tuple.Create<int, Action>(600, () =>
            {
                Interop.RECT work, wr;
                WindowWorkArea(out work, out wr);
                SetWindowPhysical(work.Left + 120, work.Top + 90);
                UpdateLayout();
                Program.Log("  摆到 " + Pos());
                Shot("pos-start");
            }));
            steps.Add(Tuple.Create<int, Action>(500, () =>
            {
                Program.Log("  点置顶 前 " + Pos());
                _vm.ToggleTopmost();
                Program.Log("  点置顶 后 " + Pos() + "   置顶=" + Topmost);
            }));
            steps.Add(Tuple.Create<int, Action>(500, () =>
            {
                _vm.ToggleTopmost();
                Program.Log("  再点置顶后 " + Pos() + "   置顶=" + Topmost);
            }));
            steps.Add(Tuple.Create<int, Action>(500, () =>
            {
                Program.Log("  折叠 前 " + Pos());
                ToggleCollapse();
                Program.Log("  折叠 后 " + Pos());
                Shot("pos-collapsed");
            }));
            steps.Add(Tuple.Create<int, Action>(500, () =>
            {
                ToggleCollapse();
                Program.Log("  展开 后 " + Pos());
                Shot("pos-expanded");
            }));
            steps.Add(Tuple.Create<int, Action>(400, () =>
            {
                _vm.HideCollapseButton = true;
                Program.Log(string.Format("  打开「隐藏折叠按钮」-> 未折叠时 ShowCollapseButton={0}（应为 False）",
                    _vm.ShowCollapseButton));
                Shot("pos-nobtn");
            }));
            steps.Add(Tuple.Create<int, Action>(500, () =>
            {
                ToggleCollapse();
                Program.Log(string.Format("  折叠着的时候 ShowCollapseButton={0}（应为 True，否则展不开）",
                    _vm.ShowCollapseButton));
                Shot("pos-nobtn-collapsed");
                ToggleCollapse();
                _vm.HideCollapseButton = false;
            }));
            steps.Add(Tuple.Create<int, Action>(400, () =>
            {
                Program.Log("  位置流程结束");
                Close();
            }));
            RunSteps(steps, 0);
        }

        /// <summary>
        /// 验收「倒垃圾」和四个标签页。倒垃圾是整个程序里唯一的不可逆操作，
        /// 必须实测它只动使命 Todo，初心 Wantdo 一条都不能少。
        /// </summary>
        void ScheduleFlowTrash()
        {
            var steps = new List<Tuple<int, Action>>();
            steps.Add(Tuple.Create<int, Action>(600, () =>
            {
                _vm.View = "bin";
                _vm.BinTab = "todo";
                Program.Log(string.Format("  倒垃圾前：使命 Todo {0} 条，初心 Wantdo {1} 条",
                    _vm.TodoBins.Count, _vm.WantBins.Count));
                Shot("bin-todo");
            }));
            steps.Add(Tuple.Create<int, Action>(450, () =>
            {
                _vm.BinTab = "want";
                foreach (var b in _vm.WantBins) Program.Log("    初心 · " + b.Caption);
                Shot("bin-want");
            }));
            steps.Add(Tuple.Create<int, Action>(450, () =>
            {
                _vm.BinTab = "todo";
                _vm.EmptyTrashCmd.Execute(null);
                Program.Log(string.Format("  点第 1 下 -> ConfirmingEmpty={0} 按钮文字=\"{1}\"（此时不该删任何东西）",
                    _vm.ConfirmingEmpty, _vm.EmptyTrashText));
                Shot("bin-confirm");
            }));
            steps.Add(Tuple.Create<int, Action>(450, () =>
            {
                _vm.EmptyTrashCmd.Execute(null);
                Program.Log(string.Format("  点第 2 下 -> 使命 Todo {0} 条，初心 Wantdo {1} 条",
                    _vm.TodoBins.Count, _vm.WantBins.Count));
                foreach (var b in _vm.WantBins) Program.Log("    初心仍在 · " + b.Caption);
                Shot("bin-after");
            }));
            steps.Add(Tuple.Create<int, Action>(450, () =>
            {
                _vm.View = "done";
                _vm.DoneTab = "todo";
                Program.Log(string.Format("  Done：Todo {0} 组，WD {1} 组",
                    _vm.TodoDoneGroups.Count, _vm.WantDoneGroups.Count));
                Shot("done-todo");
            }));
            steps.Add(Tuple.Create<int, Action>(450, () =>
            {
                _vm.DoneTab = "want";
                Shot("done-want");
            }));
            steps.Add(Tuple.Create<int, Action>(400, () =>
            {
                Program.Log("  倒垃圾 / 标签页流程结束");
                Close();
            }));
            RunSteps(steps, 0);
        }

        // ------------------------------------------------------------ UI 验收流程

        /// <summary>抓某个别的窗口（归类窗 / 时间面板）自己那块。</summary>
        void ShotWindow(Window w, string name)
        {
            try
            {
                UpdateLayout();
                Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);
                System.Threading.Thread.Sleep(180);
                Interop.RECT r;
                Interop.GetWindowRect(new WindowInteropHelper(w).Handle, out r);
                Capture.SaveRegion(Path.Combine(Program.Root, ".guide", "native-" + name + ".png"), r, 14);
                Program.Log("  截图（" + name + "）-> .guide\\native-" + name + ".png");
            }
            catch (Exception ex) { Program.Log("  !! 截窗口失败：" + ex.Message); }
        }

        void Shot(string name)
        {            // 先把 WPF 的布局+渲染跑完，再等 DWM 合成一帧。
            // 不然切完标签页立刻抓屏，抓到的是上一帧 —— 验收流程会「慢半拍」，
            // 截出来的图和日志对不上，比没有证据更糟。
            UpdateLayout();
            Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);
            System.Threading.Thread.Sleep(180);

            Interop.RECT r;
            Interop.GetWindowRect(_hwnd, out r);
            Program.Log(string.Format("  [Shot {0}] 窗口物理 ({1},{2})-({3},{4}) pad={5}",
                name, r.Left, r.Top, r.Right, r.Bottom, Program.ShotPad));
            Capture.SaveRegion(Path.Combine(Program.Root, ".guide", "native-" + name + ".png"), r, Program.ShotPad);
            Program.Log("  截图 -> .guide\\native-" + name + ".png");
        }

        static void RunSteps(List<Tuple<int, Action>> steps, int i)
        {
            if (i >= steps.Count) return;
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(steps[i].Item1) };
            t.Tick += (s, e) => { t.Stop(); steps[i].Item2(); RunSteps(steps, i + 1); };
            t.Start();
        }

        /// <summary>
        /// 验收折叠 / 置顶 / 贴边隐藏三件事：展开 → 折叠 → 展开 → 推出右边缘贴边 → 滑出 → 取消。
        /// </summary>
        void ScheduleFlowUi()
        {
            var steps = new List<Tuple<int, Action>>();
            steps.Add(Tuple.Create<int, Action>(700, () =>
            {
                Shot("ui-expanded");
                Interop.RECT wr;
                Interop.GetWindowRect(_hwnd, out wr);
                Program.Log(string.Format("  展开：窗口物理 {0}x{1} 布局 {2}x{3} 缩放 {4:0.##} 置顶 {5}",
                    wr.Right - wr.Left, wr.Bottom - wr.Top, (int)ActualWidth, (int)ActualHeight,
                    Interop.ScaleOfWindow(_hwnd), Topmost));
                ToggleCollapse();
            }));
            steps.Add(Tuple.Create<int, Action>(600, () =>
            {
                Interop.RECT wr;
                Interop.GetWindowRect(_hwnd, out wr);
                Program.Log(string.Format("  折叠后窗口物理 {0}x{1}（顶栏内容量到 {2}x{3}）",
                    wr.Right - wr.Left, wr.Bottom - wr.Top,
                    (int)Header.DesiredSize.Width, (int)Header.DesiredSize.Height));
                Shot("ui-collapsed");
                ToggleCollapse();
            }));
            steps.Add(Tuple.Create<int, Action>(600, () =>
            {
                Interop.RECT work, wr;
                WindowWorkArea(out work, out wr);
                int w = wr.Right - wr.Left;
                Program.Log(string.Format("  推出右边缘：屏右界 {0}，窗口推到 {1}（越界 {2}/{3}）",
                    work.Right, work.Right - 100, w - 100, w));
                SetWindowPhysical(work.Right - 100, wr.Top);
                MaybeDock();
            }));
            steps.Add(Tuple.Create<int, Action>(500, () => { Shot("ui-docked"); RevealDock(true); }));
            steps.Add(Tuple.Create<int, Action>(700, () => { Shot("ui-revealed"); Undock(); }));
            steps.Add(Tuple.Create<int, Action>(500, () =>
            {
                Program.Log("  UI 验收流程结束");
                Close();
            }));
            RunSteps(steps, 0);
        }

        /// <summary>
        /// 把鼠标挪到 150% 那块屏上，再走一次完整截图流程 ——
        /// 由 OverlayWindow 自己合成鼠标拖拽和点 ✓。
        /// </summary>
        void ScheduleFlowSynthDrag()
        {
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            t.Tick += (s, e) =>
            {
                t.Stop();
                int cx, cy;
                double sc;
                if (!Interop.FindScaledMonitorCenter(out cx, out cy, out sc))
                {
                    Program.Log("  !! 没找到缩放不是 100% 的显示器，跳过");
                    Close();
                    return;
                }
                Program.Log(string.Format("  把鼠标挪到缩放 {0:0.##}x 的屏中心 ({1},{2})", sc, cx, cy));
                Interop.SetCursorPos(cx, cy);

                var t2 = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
                t2.Tick += (a, b) => { t2.Stop(); DoCapture("synthdrag"); };
                t2.Start();

                // 整条流程跑完后收尾
                var end = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(6000) };
                end.Tick += (a, b) => { end.Stop(); Program.Log("  合成拖拽流程结束"); Close(); };
                end.Start();
            };
            t.Start();
        }

        /// <summary>
        /// 验收消散动画：找到第一条「已完成的 Todo」，在它的容器上跑一次 Dissolve，
        /// 中途截一张（看得到淡出中的半透明卡片），动画结束后确认它被收走了。
        /// </summary>
        void ScheduleFlowDissolve()
        {
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
            t.Tick += (s, e) =>
            {
                t.Stop();
                int idx = -1;
                for (int i = 0; i < _vm.Todos.Count; i++)
                    if (_vm.Todos[i].IsDone) { idx = i; break; }

                if (idx < 0) { Program.Log("  !! 没有「已完成的 Todo」可用来演示消散"); Close(); return; }

                var container = TodoList.ItemContainerGenerator.ContainerFromIndex(idx) as FrameworkElement;
                if (container == null) { Program.Log("  !! 容器还没生成，拿不到卡片"); Close(); return; }

                string id = _vm.Todos[idx].Id;
                Program.Log("  消散第 " + idx + " 条：" + _vm.Todos[idx].Title);

                Dissolve(container, () => { _store.File_(id); _vm.Refresh(); });

                // 中途截一张：动画 300ms，这里大约在 60% 处
                var mid = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
                mid.Tick += (a, b) =>
                {
                    mid.Stop();
                    Interop.RECT r;
                    Interop.GetWindowRect(_hwnd, out r);
                    Capture.SaveRegion(Path.Combine(Program.Root, ".tmp", "dissolve-mid.png"), r, 40);
                    Program.Log("  消散中截图 -> .tmp\\dissolve-mid.png");
                };
                mid.Start();

                var end = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
                end.Tick += (a, b) =>
                {
                    end.Stop();
                    var it = _store.Get(id);
                    Program.Log(string.Format("  ✔ 消散后：state={0} filedAt={1}",
                        J.Str(it, "state", ""), J.IsNull(J.Get(it, "filedAt")) ? "(null)" : "已收走"));
                    Program.Log("  Todo 列表剩下 " + _vm.Todos.Count + " 条，NDone=" + _vm.NDone);
                    Close();
                };
                end.Start();
            };
            t.Start();
        }

        /// <summary>验收记录窗：挑一条合适的条目打开它，等布局稳了截图退出。</summary>
        void ScheduleFlowRecord(string mode)
        {
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            t.Tick += (s, e) =>
            {
                t.Stop();
                string target = null;
                foreach (var o in _store.Items)
                {
                    var it = J.Obj(o);
                    bool isWant = J.Str(it, "room", "") == "want";
                    string state = J.Str(it, "state", "");
                    bool hasImg = !J.IsNull(J.Get(it, "image"));
                    if (mode == "view")
                    {
                        if (state == "done" && J.Str(it, "reflection", "").Length > 0) { target = J.Str(it, "id", ""); break; }
                        if (target == null && state == "done") target = J.Str(it, "id", "");
                    }
                    else
                    {
                        if (isWant && state == "active" && hasImg) { target = J.Str(it, "id", ""); break; }
                    }
                }
                if (target == null) { Program.Log("  !! 没有合适的条目可开记录窗（mode=" + mode + "）"); Close(); return; }

                Program.Log("  打开记录窗 id=" + target + " mode=" + mode);
                OpenRecord(target, mode);

                var t2 = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
                t2.Tick += (a, b) =>
                {
                    t2.Stop();
                    if (_records.Count > 0)
                    {
                        Interop.RECT r;
                        Interop.GetWindowRect(new WindowInteropHelper(_records[0]).Handle, out r);
                        Capture.SaveRegion(Program.ShotPath, r, 60);
                        Program.Log(string.Format("  记录窗截图 -> {0}  尺寸 {1}x{2}",
                            Program.ShotPath, r.Right - r.Left, r.Bottom - r.Top));
                    }
                    Close();
                };
                t2.Start();
            };
            t.Start();
        }

        /// <summary>验收详情窗：挑第一条有图的条目打开它，等布局稳了截图退出。</summary>
        void ScheduleFlowDetail()
        {
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            t.Tick += (s, e) =>
            {
                t.Stop();
                // 优先挑一条「有图的 active Wantdo」—— 能同时看到生命值行和 Get Done 按钮
                string target = null, fallback = null;
                foreach (var o in _store.Items)
                {
                    var it = J.Obj(o);
                    bool hasImg = !J.IsNull(J.Get(it, "image"));
                    if (fallback == null && hasImg) fallback = J.Str(it, "id", "");
                    if (hasImg && J.Str(it, "room", "") == "want" && J.Str(it, "state", "") == "active")
                    {
                        target = J.Str(it, "id", "");
                        break;
                    }
                }
                if (target == null) target = fallback;
                if (target == null && _store.Items.Count > 0)
                    target = J.Str(J.Obj(_store.Items[0]), "id", "");
                if (target == null) { Program.Log("  !! 没有条目可开详情"); Close(); return; }

                Program.Log("  打开详情窗 id=" + target);
                OpenDetail(target);

                var t2 = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
                t2.Tick += (a, b) =>
                {
                    t2.Stop();
                    if (_details.Count > 0)
                    {
                        Interop.RECT r;
                        Interop.GetWindowRect(new WindowInteropHelper(_details[0]).Handle, out r);
                        Capture.SaveRegion(Program.ShotPath ?? Path.Combine(Program.Root, ".tmp", "flow-detail.png"), r, 60);
                        Program.Log(string.Format("  详情窗截图 -> {0}  尺寸 {1}x{2}",
                            Program.ShotPath, r.Right - r.Left, r.Bottom - r.Top));
                    }
                    Close();
                };
                t2.Start();
            };
            t.Start();
        }

        /// <summary>
        /// 端到端验收：截图 → 归类窗（自动选 Soon）→ 回面板 → 截图 → 退出。
        /// 用来验证「截图落到正确房间」这条链路，不需要人点。
        /// </summary>
        void ScheduleFlowDemo()
        {
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
            t.Tick += (s, e) => { t.Stop(); DoCapture("flow"); };
            t.Start();
        }

        void OnClosed(object sender, EventArgs e)
        {
            _closing = true;
            if (_hwnd != IntPtr.Zero)
            {
                Interop.UnregisterHotKey(_hwnd, HK_CAPTURE);
                Interop.UnregisterHotKey(_hwnd, HK_TOGGLE);
                Interop.UnregisterHotKey(_hwnd, HK_QUIT);
            }
            if (_preview != null) _preview.Close();
        }

        IntPtr WndProc(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled)
        {
            if (msg == Interop.WM_HOTKEY)
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

        // ------------------------------------------------------------ 拖窗 / 折叠

        /// <summary>
        /// 用户在别处又双击了一次 exe（已经被单实例挡掉，那个进程自己退了）。
        /// 他多半是因为找不到面板才又点了一次，所以这里把它弄回眼前：
        /// 贴边藏着就滑出来、折叠着就这样、隐藏了就显示，然后抢焦点。
        /// </summary>
        public void ActivateFromSecondLaunch()
        {
            if (_docked) RevealDock(true);
            if (!IsVisible) Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            // 不置顶的话可能被别的窗口压着，Activate 看不出效果
            if (!_vm.AlwaysOnTop)
            {
                Topmost = true;
                Topmost = false;
            }
        }

        void OnHeaderDrag(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;
            HidePreview();
            try { DragMove(); } catch (Exception) { }
            MaybeDock();   // 松手之后看看是不是被拖出屏了
        }

        // ------------------------------------------------------------ 贴边隐藏

        /// <summary>窗口当前所在那块屏的工作区（物理像素）。</summary>
        bool WindowWorkArea(out Interop.RECT work, out Interop.RECT wr)
        {
            work = new Interop.RECT();
            wr = new Interop.RECT();
            if (_hwnd == IntPtr.Zero) return false;
            Interop.GetWindowRect(_hwnd, out wr);
            return Interop.WorkAreaAt((wr.Left + wr.Right) / 2, (wr.Top + wr.Bottom) / 2, out work);
        }

        void SetWindowPhysical(int x, int y)
        {
            if (_hwnd == IntPtr.Zero) return;
            Interop.RECT wr;
            Interop.GetWindowRect(_hwnd, out wr);
            Interop.SetWindowPos(_hwnd, Interop.HWND_TOPMOST, x, y,
                wr.Right - wr.Left, wr.Bottom - wr.Top,
                Interop.SWP_NOACTIVATE | Interop.SWP_NOZORDER);
        }

        /// <summary>
        /// 拖完松手看看：窗口被推出屏幕边界超过一定比例，就贴到那条边上去，
        /// 只留一条窄边在外面；鼠标碰一下就滑出来，移开再滑回去。
        /// </summary>
        void MaybeDock()
        {
            if (_collapsed) return;

            Interop.RECT work, wr;
            if (!WindowWorkArea(out work, out wr)) return;
            int w = wr.Right - wr.Left, h = wr.Bottom - wr.Top;
            if (w <= 0 || h <= 0) return;

            string edge = null;
            if (wr.Left < work.Left - w * DOCK_SLOP) edge = "left";
            else if (wr.Right > work.Right + w * DOCK_SLOP) edge = "right";
            else if (wr.Top < work.Top - h * DOCK_SLOP) edge = "top";
            else if (wr.Bottom > work.Bottom + h * DOCK_SLOP) edge = "bottom";

            if (edge == null) { Undock(); return; }
            Dock(edge, work, wr, w, h);
        }

        void Dock(string edge, Interop.RECT work, Interop.RECT wr, int w, int h)
        {
            int sliver = (int)Math.Round(SLIVER_DIP * Interop.ScaleOfWindow(_hwnd));

            // 弹出来的位置：整块可见，并且在那条边上对齐
            int outX, outY;
            if (edge == "left") { outX = work.Left; outY = Clamp(wr.Top, work.Top, work.Bottom - h); }
            else if (edge == "right") { outX = work.Right - w; outY = Clamp(wr.Top, work.Top, work.Bottom - h); }
            else if (edge == "top") { outX = Clamp(wr.Left, work.Left, work.Right - w); outY = work.Top; }
            else { outX = Clamp(wr.Left, work.Left, work.Right - w); outY = work.Bottom - h; }

            int parkX = edge == "left" ? work.Left - w + sliver
                      : edge == "right" ? work.Right - sliver
                      : outX;
            int parkY = edge == "top" ? work.Top - h + sliver
                      : edge == "bottom" ? work.Bottom - sliver
                      : outY;

            _docked = true;
            _dockEdge = edge;
            _dockOutX = outX; _dockOutY = outY;
            _dockParkX = parkX; _dockParkY = parkY;
            _dockRevealed = false;
            StopDockAnim();
            SetWindowPhysical(parkX, parkY);

            _vm.IsDocked = true;
            _vm.DockHint = "贴边隐藏中 · 鼠标移上去滑出来 · 拖回屏幕中间取消";
            ShowGrip(edge, true);
            Program.Log(string.Format("  贴边隐藏：{0} 藏到 ({1},{2})，弹出位置 ({3},{4})",
                edge, parkX, parkY, outX, outY));
        }

        /// <summary>
        /// 把那条紫色抓手摆到**露在外面**的那条边上。
        /// 注意是反的：靠右隐藏时窗口停在 work.Right - sliver，露出来的是窗口的**左**边。
        /// </summary>
        void ShowGrip(string edge, bool on)
        {
            if (DockGrip == null) return;
            if (!on) { DockGrip.Visibility = Visibility.Collapsed; return; }

            if (edge == "left" || edge == "right")
            {
                DockGrip.Width = 5; DockGrip.Height = 46;
                DockGrip.VerticalAlignment = VerticalAlignment.Center;
                // 靠左隐藏 -> 露出右边；靠右隐藏 -> 露出左边
                DockGrip.HorizontalAlignment = edge == "left" ? HorizontalAlignment.Right : HorizontalAlignment.Left;
                DockGrip.Margin = edge == "left" ? new Thickness(0, 0, 1, 0) : new Thickness(1, 0, 0, 0);
            }
            else
            {
                DockGrip.Width = 46; DockGrip.Height = 5;
                DockGrip.HorizontalAlignment = HorizontalAlignment.Center;
                DockGrip.VerticalAlignment = edge == "top" ? VerticalAlignment.Bottom : VerticalAlignment.Top;
                DockGrip.Margin = edge == "top" ? new Thickness(0, 0, 0, 1) : new Thickness(0, 1, 0, 0);
            }
            DockGrip.Visibility = Visibility.Visible;
        }

        static int Clamp(int v, int lo, int hi) { return hi < lo ? lo : Math.Max(lo, Math.Min(v, hi)); }

        void Undock()
        {
            if (!_docked) return;
            _docked = false;
            StopDockAnim();
            if (_dockHideTimer != null) { _dockHideTimer.Stop(); _dockHideTimer = null; }
            _vm.IsDocked = false;
            _vm.DockHint = null;
            if (DockGrip != null) DockGrip.Visibility = Visibility.Collapsed;
            _dockEdge = null;
            _dockRevealed = false;
        }

        /// <summary>
        /// 滑出 / 滑回。
        ///
        /// 用 CompositionTarget.Rendering 驱动，**每个渲染帧更新一次位置**，和 vsync 对齐。
        /// 之前用 12ms 的 DispatcherTimer：它只是个普通定时器，会被消息循环合并、
        /// 也和渲染节奏错开，所以看起来一格一格的。窗口滑动这种事必须跟着渲染帧走。
        /// 位置仍然用物理像素的 SetWindowPos，不碰 WPF 的 Left/Top（混 DPI 下会绕晕）。
        /// </summary>
        void RevealDock(bool on)
        {
            if (!_docked || _dockRevealed == on) return;
            _dockRevealed = on;
            if (DockGrip != null) DockGrip.Visibility = on ? Visibility.Collapsed : Visibility.Visible;

            // 收回去的时候先等一小会儿：鼠标刚离开边缘时容易立刻又碰到那条窄边，
            // 不等一下就会来回抖
            if (!on)
            {
                if (_dockHideTimer != null) _dockHideTimer.Stop();
                _dockHideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(DOCK_HIDE_DELAY) };
                _dockHideTimer.Tick += (s, e) =>
                {
                    _dockHideTimer.Stop();
                    _dockHideTimer = null;
                    if (_docked && !_dockRevealed) StartDockAnim(_dockParkX, _dockParkY);
                };
                _dockHideTimer.Start();
                return;
            }

            if (_dockHideTimer != null) { _dockHideTimer.Stop(); _dockHideTimer = null; }
            StartDockAnim(_dockOutX, _dockOutY);
        }

        void StartDockAnim(int toX, int toY)
        {
            StopDockAnim();

            Interop.RECT wr;
            Interop.GetWindowRect(_hwnd, out wr);
            _dockFromX = wr.Left; _dockFromY = wr.Top;
            _dockToX = toX; _dockToY = toY;
            if (_dockFromX == _dockToX && _dockFromY == _dockToY) return;

            _dockClock = Stopwatch.StartNew();
            int frames = 0;
            _dockRender = (s, e) =>
            {
                double ms = _dockClock.Elapsed.TotalMilliseconds;
                double t = Math.Min(1.0, ms / DOCK_ANIM_MS);
                double k = 1 - Math.Pow(1 - t, 3);   // ease-out cubic
                SetWindowPhysical(
                    (int)Math.Round(_dockFromX + (_dockToX - _dockFromX) * k),
                    (int)Math.Round(_dockFromY + (_dockToY - _dockFromY) * k));
                frames++;
                if (t >= 1.0)
                {
                    StopDockAnim();
                    SetWindowPhysical(_dockToX, _dockToY);   // 收尾对齐，别停在中间
                    Program.Log(string.Format("  滑动完成：{0} 帧 / {1:0}ms（{2:0} fps）",
                        frames, ms, frames * 1000.0 / Math.Max(1, ms)));
                }
            };
            CompositionTarget.Rendering += _dockRender;
        }

        void StopDockAnim()
        {
            if (_dockRender == null) return;
            CompositionTarget.Rendering -= _dockRender;
            _dockRender = null;
        }

        void ToggleCollapse()
        {
            _collapsed = !_collapsed;
            _vm.Collapsed = _collapsed;
            HidePreview();
            if (_collapsed) Undock();   // 贴边藏着的时候先弹回来，不然收起来就找不着了

            double w, h;
            if (_collapsed)
            {
                // 折叠后只剩顶栏，宽度不能写死 —— 190 比顶栏实际需要的还窄（6 个按钮 + 标题
                // 差不多要 236），窗口比内容窄内容就会被挤变形。直接量顶栏，让它自己决定。
                Header.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
                w = Math.Ceiling(Header.DesiredSize.Width) + 2;    // +2 = Shell 的 1px 边框
                h = Math.Ceiling(Header.DesiredSize.Height) + 2;
                Program.Log(string.Format("  折叠尺寸：顶栏量到 {0}x{1} -> 窗口 {2}x{3}",
                    (int)Header.DesiredSize.Width, (int)Header.DesiredSize.Height, (int)w, (int)h));
            }
            else
            {
                w = PANEL_W;
                h = PANEL_H;
            }

            // 原地缩放：只改尺寸，Left/Top 一个像素都不动。
            //
            // 原来这里会 `Left = wa.Right - w - MARGIN; Top = wa.Bottom - h - MARGIN;`
            // 也就是每次折叠/展开都把面板甩到右下角 —— 用户拖到别处再折叠就被拽回去了。
            // 而且 SystemParameters.WorkArea 是**主屏**的工作区，面板在副屏上时
            // 一折叠会被直接跨屏拽回主屏。
            Width = w; Height = h;
            UpdateLayout();          // 让新尺寸立刻生效，下面读 GetWindowRect 才是准的
            ClampIntoWorkArea();
        }

        /// <summary>
        /// 把窗口拉回它**当前所在那块屏**的工作区里。尺寸变化后可能超出屏幕边界，
        /// 但位置本身不该被改动 —— 只做「拉回来」，不做「摆到某处」。
        /// </summary>
        void ClampIntoWorkArea()
        {
            Interop.RECT work, wr;
            if (!WindowWorkArea(out work, out wr)) return;
            int w = wr.Right - wr.Left, h = wr.Bottom - wr.Top;
            if (w <= 0 || h <= 0) return;

            int x = Clamp(wr.Left, work.Left, work.Right - w);
            int y = Clamp(wr.Top, work.Top, work.Bottom - h);
            if (x != wr.Left || y != wr.Top)
            {
                Program.Log(string.Format("  尺寸变了超出屏幕，拉回 ({0},{1}) -> ({2},{3})", wr.Left, wr.Top, x, y));
                SetWindowPhysical(x, y);
            }
        }

        // ------------------------------------------------------------ 卡片交互

        static string IdOf(object dc)
        {
            var t = dc as TodoVm; if (t != null) return t.Id;
            var w = dc as WantVm; if (w != null) return w.Id;
            var b = dc as BinVm; if (b != null) return b.Id;
            var d = dc as DoneVm; if (d != null) return d.Id;
            return null;
        }

        static string ImgOf(object dc)
        {
            var t = dc as TodoVm; if (t != null) return t.ImagePath;
            var w = dc as WantVm; if (w != null) return w.ImagePath;
            var b = dc as BinVm; if (b != null) return b.ImagePath;
            return null;
        }

        void OnTodoCardClick(object sender, MouseButtonEventArgs e)
        {
            HidePreview();
            var fe = sender as FrameworkElement;
            if (fe == null) return;
            string id = IdOf(fe.DataContext);
            if (id == null) return;

            // 已完成的 Todo 再点一下：先让它「化掉」，动画走完再收进完成列表。
            // 和 panel.css 的 bubble-out 一致：微放大 + 淡出 + 高度收到 0。
            var it = _store.Get(id);
            if (it != null && J.Str(it, "room", "") == "todo" && J.Str(it, "state", "") == "done")
            {
                Dissolve(fe, () => { _store.File_(id); _vm.Refresh(); });
                return;
            }
            _vm.OpenCmd.Execute(id);
        }

        /// <summary>
        /// 泡沫消散：0.3s 里同时做微放大、淡出、高度收到 0。
        /// 必须先把真实高度写死成 Height，动画才能从「实际高度」精确收到 0。
        /// </summary>
        static void Dissolve(FrameworkElement card, Action done)
        {
            double h = card.ActualHeight;
            card.RenderTransformOrigin = new Point(0.5, 0.5);
            var scale = new ScaleTransform(1, 1);
            card.RenderTransform = scale;
            card.Height = h;
            card.ClipToBounds = true;

            var dur = TimeSpan.FromMilliseconds(300);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var sb = new Storyboard();

            sb.Children.Add(Anim(card, "(UIElement.Opacity)", 0, dur, ease));
            sb.Children.Add(Anim(scale, "ScaleX", 1.05, dur, ease));
            sb.Children.Add(Anim(scale, "ScaleY", 1.05, dur, ease));
            sb.Children.Add(Anim(card, "Height", 0, dur, ease));

            var margin = new ThicknessAnimation(new Thickness(0, 0, 0, 6), new Thickness(0), dur) { EasingFunction = ease };
            Storyboard.SetTarget(margin, card);
            Storyboard.SetTargetProperty(margin, new PropertyPath("Margin"));
            sb.Children.Add(margin);

            sb.Completed += (s, e) => { if (done != null) done(); };
            sb.Begin();
        }

        static DoubleAnimation Anim(DependencyObject target, string path, double to, TimeSpan dur, IEasingFunction ease)
        {
            var a = new DoubleAnimation(to, dur) { EasingFunction = ease };
            Storyboard.SetTarget(a, target);
            Storyboard.SetTargetProperty(a, new PropertyPath(path));
            return a;
        }

        void OnWantCardClick(object sender, MouseButtonEventArgs e)
        {
            HidePreview();
            string id = IdOf((sender as FrameworkElement).DataContext);
            if (id != null) _vm.OpenCmd.Execute(id);
        }

        void OnBinCardClick(object sender, MouseButtonEventArgs e)
        {
            HidePreview();
            var fe = sender as FrameworkElement;
            if (fe == null) return;
            // 使命 Todo 不能捡回来 —— 舍弃就是舍弃，只能靠「倒垃圾」一次清空。
            // 初心 Wantdo 才有这一下。
            var b = fe.DataContext as BinVm;
            if (b == null || !b.CanRestore) return;
            _vm.RestoreCmd.Execute(b.Id);
        }

        void OnDoneItemClick(object sender, MouseButtonEventArgs e)
        {
            HidePreview();
            string id = IdOf((sender as FrameworkElement).DataContext);
            if (id != null) _vm.OpenDoneCmd.Execute(id);
        }

        // ------------------------------------------------------------ 悬停预览

        void OnCardMouseEnter(object sender, MouseEventArgs e)
        {
            var fe = sender as FrameworkElement;
            if (fe == null) return;
            string id = IdOf(fe.DataContext);
            string img = ImgOf(fe.DataContext);
            if (id == null || string.IsNullOrEmpty(img)) return;
            if (id == _hoverId) return;

            HidePreview();
            _hoverId = id;

            _hoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(HOVER_DELAY) };
            _hoverTimer.Tick += (s, a) =>
            {
                _hoverTimer.Stop();
                _hoverTimer = null;
                ShowPreview(fe, img);
            };
            _hoverTimer.Start();
        }

        void OnCardMouseLeave(object sender, MouseEventArgs e)
        {
            HidePreview();
        }

        void ShowPreview(FrameworkElement anchor, string imgPath)
        {
            var src = Images.Get(imgPath, 900);
            if (src == null) return;
            if (_preview == null)
            {
                _preview = new PreviewWindow { Owner = null };
                _preview.Show();
                _preview.Hide();
            }
            var p = anchor.PointToScreen(new Point(0, 0));
            // PointToScreen 给的是物理像素，Window.Left/Top 要 DIP
            var src2 = PresentationSource.FromVisual(this);
            double sx = 1, sy = 1;
            if (src2 != null && src2.CompositionTarget != null)
            {
                var m = src2.CompositionTarget.TransformToDevice;
                sx = m.M11; sy = m.M22;
            }
            _preview.ShowAt(src, new Point(p.X / sx, p.Y / sy), anchor.ActualHeight, Left);
        }

        void HidePreview()
        {
            if (_hoverTimer != null) { _hoverTimer.Stop(); _hoverTimer = null; }
            _hoverId = null;
            if (_preview != null && _preview.IsVisible) _preview.Hide();
        }

        // ------------------------------------------------------------ 详情窗

        /// <summary>
        /// 每一条各开一扇详情窗，可以同时摆好几条对照着看。
        ///
        /// 之前这里是「只留一扇」：开着 A 的时候点 B，只把 A 拉到前面，**内容都不换**，
        /// 看起来就像点了没反应。Electron 版至少还会把内容换成 B，所以这是个回归。
        /// 同一条再点一次还是跳到已经开着的那扇，不开重复的。
        /// </summary>
        void OpenDetail(string id)
        {
            foreach (var w in _details)
            {
                if (w.ItemId == id && w.IsVisible) { w.Activate(); return; }
            }

            var d = new DetailWindow(_store, id);
            d.RecordRequested += rid =>
            {
                OpenRecord(rid, "write");
                d.Close();   // 写完就把它让出来，和原来的行为一致
            };
            d.ScreenshotRequested += rid => StartCaptureForItem(rid);
            d.Closed += (s, e) => { _details.Remove(d); _vm.Refresh(); };
            _details.Add(d);
            d.Show();
            Cascade(d, _details.Count - 1);
            d.Activate();
        }

        // ------------------------------------------------------------ 完成记录窗

        void OpenRecord(string id, string mode)
        {
            foreach (var w in _records)
            {
                if (w.ItemId == id && w.IsVisible) { w.Activate(); return; }
            }

            var r = new RecordWindow(_store, id, mode);
            r.Committed += (rid, reflection) =>
            {
                // 写完心得 = 完成并收走（和 record.js 的 commit 一致）
                _store.Done(rid, true, reflection, true);
                _vm.Refresh();
            };
            r.Closed += (s, e) => { _records.Remove(r); _vm.Refresh(); };
            _records.Add(r);
            r.Show();
            Cascade(r, _records.Count - 1);
            r.Activate();
        }

        /// <summary>
        /// 多扇窗别叠成一摞：每开一扇往下右各错开一点。
        /// 错到快出屏了就绕回起点，免得第 8 扇开到屏幕外面去。
        /// </summary>
        static void Cascade(Window w, int index)
        {
            const double STEP = 26;
            const int WRAP = 6;
            double off = (index % WRAP) * STEP;
            try
            {
                var wa = SystemParameters.WorkArea;
                double width = w.Width > 0 ? w.Width : 420;
                double height = w.Height > 0 ? w.Height : 520;
                double x = wa.Right - width - 40 - off;
                double y = wa.Bottom - height - 40 - off;
                // 别撞到工作区左上角外面
                w.Left = Math.Max(wa.Left + 12, x);
                w.Top = Math.Max(wa.Top + 12, y);
            }
            catch (Exception) { }
        }

        // ------------------------------------------------------------ 快速记录

        void OnQuickKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                _vm.QuickOpen = false;
                e.Handled = true;
                return;
            }
            if (e.Key != Key.Enter) return;
            _vm.AddManualCmd.Execute(null);
            e.Handled = true;
        }

        void OnQuickAdd(object sender, RoutedEventArgs e)
        {
            StartManualAdd();
        }

        /// <summary>
        /// 「手动记一笔」的加号。
        ///
        /// Todo 是有 DDL 的东西，**没期限的 Todo 是自相矛盾的**（它永远不会逾期、也不会变红）。
        /// 所以选了 Todo 就让用户挑个时间 —— 复用归类窗那三个按钮，只是没有截图和标题框。
        /// Wantdo 本来就没有期限，直接落库。
        /// </summary>
        void StartManualAdd()
        {
            string title = (_vm.QuickText ?? "").Trim();
            if (title.Length == 0) return;

            if (_vm.QuickRoom != "todo")
            {
                _vm.AddManualCmd.Execute(null);
                return;
            }

            Interop.RECT cur;
            double sc;
            if (!Interop.MonitorUnderCursor(out cur, out sc))
            {
                cur = new Interop.RECT
                {
                    Left = 0, Top = 0,
                    Right = Interop.GetSystemMetrics(0),
                    Bottom = Interop.GetSystemMetrics(1),
                };
                sc = 1.0;
            }

            var p = new ChooserWindow.Payload { Title = title, App = "", Manual = true };
            var w = new ChooserWindow(p, cur, sc, 0, 0, 0, 0);
            _chooser = w;   // 验收流程要能驱动它
            w.Decided += (choice, typed, days) =>
            {
                if (choice == null) return;   // 取消：什么都不做，输入框里的字留着
                string room = choice == "want" ? "want" : "todo";
                string bucket = choice == "today" ? "today" : (choice == "soon" ? "soon" : null);
                _store.Add(room, title, "", null, bucket, null, days);
                _vm.QuickText = "";
                _vm.QuickOpen = false;
                _vm.Refresh();
                Program.Log(string.Format("  手动记一笔：room={0} bucket={1} days={2}", room, bucket, days));
            };
            w.Show();
            w.Activate();
        }

        /// <summary>点色块换背景色。色块是 StackPanel 不是 Button（Button 的模板会套个框，很丑）。</summary>
        void OnThemeSwatchClick(object sender, MouseButtonEventArgs e)
        {
            var fe = sender as FrameworkElement;
            var opt = fe == null ? null : fe.DataContext as ThemeOptionVm;
            if (opt != null) _vm.PanelTheme = opt.Key;
        }

        // ------------------------------------------------------------ 设置页滑块

        void OnOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            // 拖动时只做本地预览，不写盘
            if (IsLoaded) { _vm.OpacityPreview = _vm.PanelAlpha; }
        }

        void OnOpacityCommitted(object sender, DragCompletedEventArgs e)
        {
            _vm.OpacityPreview = null;
            _vm.SaveOpacity();
        }

        void OnDecayCommitted(object sender, DragCompletedEventArgs e)
        {
            _vm.SaveDecay();
        }

        // ------------------------------------------------------------ 截图

        /// <summary>
        /// 截图流程第一步：先把面板撤掉（别把自己截进去），再抓鼠标所在那块屏，弹框选层。
        /// </summary>
        void DoCapture(string trigger)
        {
            if (_inCapture) return;   // 已经在流程里了，别叠
            _inCapture = true;
            _captureHadPanel = IsVisible;
            HidePreview();
            if (_captureHadPanel) Hide();

            // 等 DWM 真把面板从屏幕上撤掉，否则截到的图里还留着半透明的残影
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
            t.Tick += (s, e) => { t.Stop(); GrabAndOverlay(trigger); };
            t.Start();
        }

        /// <summary>
        /// 抓屏 + 在**每一块屏**上都铺一层框选层。
        ///
        /// 之前只盖鼠标所在那块屏，于是「在主屏按的截图键，就没法去副屏框选」。
        /// 现在每块屏各有一层（各铺自己那块屏的截图），在哪块上拖就在哪块里选。
        /// 单块屏内跨屏拖拽仍然不支持 —— 混 DPI 下一条窗口跨两块屏没法同时正确渲染，
        /// 那是要拿一整张虚拟屏位图去硬扛的另一种做法。
        /// </summary>
        void GrabAndOverlay(string trigger)
        {
            Interop.RECT cur;
            double curScale;
            if (!Interop.MonitorUnderCursor(out cur, out curScale))
            {
                cur = new Interop.RECT
                {
                    Left = 0, Top = 0,
                    Right = Interop.GetSystemMetrics(0),
                    Bottom = Interop.GetSystemMetrics(1),
                };
                curScale = 1.0;
            }

            var mons = Interop.AllMonitors();
            if (mons.Count == 0) mons.Add(cur);
            // 光标那块屏排最前：它先显示、先拿焦点、提示条也最显眼
            mons.Sort((a, b) =>
                (Interop.SameRect(a, cur) ? 0 : 1).CompareTo(Interop.SameRect(b, cur) ? 0 : 1));

            string app, title;
            Interop.ForegroundInfo(_hwnd, out app, out title);

            var shots = new List<BitmapSource>();
            var rects = new List<Interop.RECT>();
            long totalGrab = 0;
            foreach (var m in mons)
            {
                int w = Math.Max(1, m.Right - m.Left), h = Math.Max(1, m.Bottom - m.Top);
                long g;
                BitmapSource src;
                using (var bmp = Capture.GrabRect(m.Left, m.Top, w, h, out g)) src = ToSource(bmp);
                shots.Add(src);
                rects.Add(m);
                totalGrab += g;
            }
            _grabMs.Add(totalGrab);

            Program.Log(string.Format(
                "  抓屏[{0}] 共 {1} 块屏 grab={2}ms 来源=\"{3}\" 标题=\"{4}\"",
                trigger, mons.Count, totalGrab, app, title));
            for (int i = 0; i < rects.Count; i++)
            {
                var m = rects[i];
                Program.Log(string.Format("    屏{0} ({1},{2}) {3}x{4} @{5:0.##}x",
                    i, m.Left, m.Top, m.Right - m.Left, m.Bottom - m.Top, Interop.ScaleOf(m)));
            }

            // 基准测试不弹框选层：光标那块屏整屏当选区直接走
            if (Program.Bench)
            {
                var m0 = rects[0];
                ChooseFor(shots[0], m0, Interop.ScaleOf(m0),
                    0, 0, m0.Right - m0.Left, m0.Bottom - m0.Top, app, title);
                return;
            }

            var ovs = new List<OverlayWindow>();
            var quick = Program.ForceQuick || _vm.QuickCapture;
            for (int i = 0; i < rects.Count; i++)
            {
                var src = shots[i];
                var mon = rects[i];
                double sc = Interop.ScaleOf(mon);

                var ov = new OverlayWindow(src, mon, sc, quick);
                if (i > 0) ov.ShowActivated = false;   // 只有光标那块抢焦点
                ov.Picked += (x, y, w, h) =>
                {
                    DismissOverlays(ovs);
                    ChooseFor(src, mon, sc, x, y, w, h, app, title);
                };
                ov.Cancelled += () => { DismissOverlays(ovs); EndCapture(); };
                ovs.Add(ov);
            }
            foreach (var o in ovs) o.Show();
            if (ovs.Count > 0) ovs[0].Activate();
        }

        static void DismissOverlays(List<OverlayWindow> ovs)
        {
            foreach (var o in ovs)
            {
                if (o == null) continue;
                try { o.Dismiss(); } catch (Exception) { }
            }
        }

        /// <summary>裁出选区 → 编 PNG → 弹归类窗（或挂到已有条目上）。</summary>
        void ChooseFor(BitmapSource full, Interop.RECT mon, double scale, int x, int y, int w, int h, string app, string title)
        {
            byte[] png;
            BitmapSource preview;
            var sw = Stopwatch.StartNew();
            try
            {
                var crop = new CroppedBitmap(full, new Int32Rect(x, y, w, h));
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(crop));
                using (var ms = new MemoryStream()) { enc.Save(ms); png = ms.ToArray(); }
                preview = MakePreview(png);
            }
            catch (Exception ex)
            {
                Program.Log("  !! 裁剪失败：" + ex.Message);
                EndCapture();
                return;
            }

            Program.Log(string.Format("  裁剪 {0}x{1} -> PNG {2}KB，耗时 {3}ms",
                w, h, png.Length / 1024, sw.ElapsedMilliseconds));

            var payload = new ChooserWindow.Payload
            {
                Png = png,
                ThumbPng = MakeThumbPng(preview),
                Preview = preview,
                App = app,
                Title = title,
            };

            // 补截图：结果挂到已有条目上，不弹归类窗（归类早就定过了）
            if (_attachToId != null)
            {
                string to = _attachToId;
                _attachToId = null;
                AttachImage(to, payload);
                EndCapture();
                return;
            }

            if (Program.Bench)
            {
                CommitCapture(payload, "want", null);
                EndCapture();
                return;
            }

            var chooser = new ChooserWindow(payload, mon, scale, x, y, w, h);
            chooser.Decided += (choice, typed, days) =>
            {
                if (choice != null) CommitCapture(payload, choice, typed, days);
            };
            chooser.Closed += (s, e) =>
            {
                _chooser = null;
                if (_closing) return;
                EndCapture();
                if (Program.Flow == "demo") RunDemoVerification();
            };
            _chooser = chooser;
            chooser.Show();
            chooser.Activate();
            AutoChooseIfSynthDrag();
        }

        /// <summary>合成拖拽验收时，归类窗自己选一个，免得整条流程卡在那儿等人点。</summary>
        void AutoChooseIfSynthDrag()
        {
            if (Program.Flow != "synthdrag") return;
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            t.Tick += (s, e) =>
            {
                t.Stop();
                if (_chooser != null && _chooser.IsVisible) _chooser.AutoPick("want");
            };
            t.Start();
        }

        /// <summary>整条流程收尾：把面板放回来、刷新、清掉流程标记。</summary>
        void EndCapture()
        {
            _inCapture = false;
            if (_captureHadPanel && !_closing)
            {
                Show();
                Activate();
            }
            _vm.Refresh();
        }

        /// <summary>
        /// 基准测试用的一次完整采集：撤面板 → 抓屏 → 编 PNG。
        /// 刻意绕开 DoCapture 那套异步流程 —— 那条路会等框选层，量不到真实耗时。
        /// </summary>
        void BenchCaptureOnce()
        {
            var sw = Stopwatch.StartNew();
            bool wasVisible = IsVisible;
            if (wasVisible) Hide();
            System.Threading.Thread.Sleep(140);
            long hideMs = sw.ElapsedMilliseconds;

            Interop.RECT mon;
            double scale;
            if (!Interop.MonitorUnderCursor(out mon, out scale))
            {
                mon = new Interop.RECT { Left = 0, Top = 0, Right = Interop.GetSystemMetrics(0), Bottom = Interop.GetSystemMetrics(1) };
            }
            int mw = Math.Max(1, mon.Right - mon.Left), mh = Math.Max(1, mon.Bottom - mon.Top);

            long grabMs, pngMs;
            int bytes;
            using (var bmp = Capture.GrabRect(mon.Left, mon.Top, mw, mh, out grabMs))
            {
                byte[] png = Capture.ToPng(bmp, out pngMs);
                bytes = png.Length;
            }

            _grabMs.Add(grabMs);
            _captureMs.Add(sw.ElapsedMilliseconds);
            Program.Log(string.Format("  采集[bench] hide={0}ms grab={1}ms png={2}ms({3}KB) 共={4}ms {5}x{6}",
                hideMs, grabMs, pngMs, bytes / 1024, sw.ElapsedMilliseconds, mw, mh));

            if (wasVisible) { Show(); Activate(); }
        }

        /// <summary>--flow=demo 的落库验证：截面板、打日志、退出。</summary>
        void RunDemoVerification()
        {
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
            t.Tick += (a, b) =>
            {
                t.Stop();
                Interop.RECT r;
                Interop.GetWindowRect(_hwnd, out r);
                Capture.SaveRegion(Program.ShotPath ?? Path.Combine(Program.Root, ".tmp", "flow-panel.png"), r, 80);

                var it = _store.Items.Count > 0 ? J.Obj(_store.Items[_store.Items.Count - 1]) : null;
                if (it != null)
                {
                    Program.Log(string.Format(
                        "  ✔ 落库验证：room={0} dueBucket={1} title=\"{2}\" source=\"{3}\" image={4}",
                        J.Str(it, "room", ""), J.Str(it, "dueBucket", null) ?? "(null)",
                        J.Str(it, "title", ""), J.Str(it, "source", ""),
                        J.Str(it, "image", null) ?? "(null)"));
                }
                Program.Log("  条目总数 = " + _store.Items.Count);
                Close();
            };
            t.Start();
        }

        /// <summary>GDI Bitmap → WPF BitmapSource（拷一份像素，之后 GDI 位图就能扔了）。</summary>
        static BitmapSource ToSource(Bitmap bmp)
        {
            IntPtr h = bmp.GetHbitmap();
            try
            {
                var src = Imaging.CreateBitmapSourceFromHBitmap(
                    h, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }
            finally { Interop.DeleteObject(h); }
        }

        /// <summary>
        /// 标题优先级：用户手填 > 截图时的前台窗口标题 > 空（和 main.js 的 commitCapture 一致）。
        /// </summary>
        /// <summary>
        /// 给一条已有条目补一张截图。走的是**和截图主流程同一套框选层** ——
        /// 不多写一份抓屏/选区代码，行为也就自然一致（多屏、混 DPI 都白拿）。
        /// </summary>
        public void StartCaptureForItem(string itemId)
        {
            if (string.IsNullOrEmpty(itemId)) return;
            var it = _store.Get(itemId);
            if (it == null) return;
            _attachToId = itemId;
            Program.Log("  补截图：目标 " + itemId.Substring(0, 8) + "「" + J.Str(it, "title", "") + "」");
            DoCapture("补图");
        }

        /// <summary>把刚截下来的图挂到指定条目上（替换原图；缩略图缓存一并重写）。</summary>
        void AttachImage(string id, ChooserWindow.Payload p)
        {
            var it = _store.Get(id);
            if (it == null) { Program.Log("  !! 补图目标已不存在"); return; }
            try
            {
                string rel = _store.SaveImage(id, p.Png);
                it["image"] = rel;
                _store.Save();
                if (p.ThumbPng != null)
                {
                    string thumbRel = Path.ChangeExtension(rel, null) + ".thumb.png";
                    File.WriteAllBytes(Path.Combine(_store.DataDir,
                        thumbRel.Replace('/', Path.DirectorySeparatorChar)), p.ThumbPng);
                }
                Program.Log("  补图成功 -> " + rel + "（" + p.Png.Length / 1024 + "KB）");
            }
            catch (Exception ex)
            {
                Program.Log("  !! 补图失败：" + ex.Message);
            }
            // 已经开着的那扇详情窗要重读，不然它还显示着「补一张截图」的加号
            foreach (var w in _details)
            {
                if (w.ItemId == id && w.IsVisible) { try { w.Reload(); } catch (Exception) { } }
            }
            _vm.Refresh();
        }

        void CommitCapture(ChooserWindow.Payload p, string choice, string typedTitle, int dueDays = 0)
        {
            string room = choice == "want" ? "want" : "todo";
            string dueBucket = choice == "today" ? "today" : (choice == "soon" ? "soon" : null);
            string title = string.IsNullOrEmpty(typedTitle) ? (p.Title ?? "") : typedTitle;

            var item = _store.Add(room, title, p.App ?? "", null, dueBucket, null, dueDays);
            try
            {
                string rel = _store.SaveImage(J.Str(item, "id", ""), p.Png);
                item["image"] = rel;
                _store.Save();

                // 顺手写一份缩略图：下次启动读小图就行，不用整张解码 7MB 的截图
                if (p.ThumbPng != null)
                {
                    string thumbRel = Path.ChangeExtension(rel, null) + ".thumb.png";
                    File.WriteAllBytes(Path.Combine(_store.DataDir,
                        thumbRel.Replace('/', Path.DirectorySeparatorChar)), p.ThumbPng);
                }
            }
            catch (Exception ex)
            {
                // 存图失败不该让条目丢失，退化为纯文本条目
                Program.Log("  !! 存图失败：" + ex.Message);
            }
            _vm.Refresh();
        }

        /// <summary>
        /// 按 256px 宽解码：既够归类窗那个 64×46 的小图用，也正好当落库的缩略图尺寸，
        /// 不用为了一个缩略图把整张 2560×1440 留在内存里。
        /// </summary>
        static BitmapSource MakePreview(byte[] png)
        {
            try
            {
                var b = new BitmapImage();
                b.BeginInit();
                b.CacheOption = BitmapCacheOption.OnLoad;
                b.DecodePixelWidth = 256;
                b.StreamSource = new MemoryStream(png);
                b.EndInit();
                b.Freeze();
                return b;
            }
            catch (Exception) { return null; }
        }

        /// <summary>把已经解好的 256px 预览重新编成缩略图存盘，供下次启动秒开。</summary>
        static byte[] MakeThumbPng(BitmapSource preview)
        {
            if (preview == null) return null;
            try
            {
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(preview));
                using (var ms = new MemoryStream())
                {
                    enc.Save(ms);
                    return ms.ToArray();
                }
            }
            catch (Exception) { return null; }
        }

        // ------------------------------------------------------------ 基准测试 / 截图

        void ScheduleBench()
        {
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Program.BenchDelayMs) };
            t.Tick += (s, e) => { t.Stop(); RunBench(); };
            t.Start();
        }

        void RunBench()
        {
            if (!Program.NoCapture) { for (int i = 0; i < 4; i++) BenchCaptureOnce(); }

            var me = Process.GetCurrentProcess();
            me.Refresh();

            long exeBytes = 0;
            int exeCount = 0;
            foreach (var f in Directory.GetFiles(AppDomain.CurrentDomain.BaseDirectory))
            {
                exeBytes += new FileInfo(f).Length;
                exeCount++;
            }

            var result = new Dictionary<string, object>
            {
                { "mode", "native" },
                { "processCount", 1.0 },
                { "selfWsMB", Math.Round(me.WorkingSet64 / 1048576.0, 1) },
                { "selfPrivMB", Math.Round(me.PrivateMemorySize64 / 1048576.0, 1) },
                { "totalWsMB", Math.Round(me.WorkingSet64 / 1048576.0, 1) },
                { "totalPrivMB", Math.Round(me.PrivateMemorySize64 / 1048576.0, 1) },
                { "threads", (double)me.Threads.Count },
                { "firstPingMs", (double)_firstPaintMs },
                { "exeBytes", (double)exeBytes },
                { "exeFileCount", (double)exeCount },
                { "captureCount", (double)_captureMs.Count },
                { "captureMs", ToDoubles(_captureMs) },
                { "grabMs", ToDoubles(_grabMs) },
            };

            string outFile = Path.Combine(Program.Root, ".tmp",
                "native-bench" + (Program.NoCapture ? "-nocap" : "") + ".json");
            Directory.CreateDirectory(Path.GetDirectoryName(outFile));
            File.WriteAllText(outFile, Json.Stringify(result), new UTF8Encoding(false));

            Program.Log("");
            Program.Log("=== 基准测试结果（纯原生 WPF，无 WebView2）===");
            Program.Log("  进程数        : 1");
            Program.Log("  工作集        : " + result["selfWsMB"] + " MB");
            Program.Log("  私有内存      : " + result["selfPrivMB"] + " MB");
            Program.Log("  线程数        : " + result["threads"]);
            Program.Log("  首帧渲染      : " + _firstPaintMs + " ms");
            Program.Log("  分发体积      : " + (exeBytes / 1048576.0).ToString("0.00") + " MB / " + exeCount + " 个文件");
            if (_grabMs.Count > 0)
            {
                Program.Log("  截图 grab     : " + string.Join(", ", ToDoubles(_grabMs).ConvertAll(x => x.ToString()).ToArray()) + " ms");
                Program.Log("  截图 全程     : " + string.Join(", ", ToDoubles(_captureMs).ConvertAll(x => x.ToString()).ToArray()) + " ms");
            }
            Program.Log("  写出         : " + outFile);
            Program.Log("");

            Close();
        }

        void ScheduleShot()
        {
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2000) };
            t.Tick += (s, e) =>
            {
                t.Stop();
                Interop.RECT r;
                Interop.GetWindowRect(_hwnd, out r);
                Program.Log(string.Format("  窗口物理矩形 = ({0},{1})-({2},{3})  尺寸 {4}x{5}",
                    r.Left, r.Top, r.Right, r.Bottom, r.Right - r.Left, r.Bottom - r.Top));
                Capture.SaveRegion(Program.ShotPath, r, 80);
                Capture.SaveFullScreen(Program.ShotPath + ".full.png");
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
