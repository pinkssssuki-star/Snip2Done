using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;

namespace RemeberEr
{
    /// <summary>
    /// 常驻面板窗口。
    ///
    /// 数据全部走 PanelViewModel（与 WPF 版同一套逻辑），窗口只负责：
    ///   · 窗口层面的平台行为（透明/无边框/置顶/右下角定位/拖动）
    ///   · 把点击/拖动这类输入翻译成对 VM 或其它窗口的调用
    ///   · 缩略图解码完之后刷新那几行（由 VM 抛事件驱动，不额外读一次盘）
    ///
    /// 一个刻意的设计：诊断面板留在这里而不是删掉。
    /// GUI 程序没有控制台，把「平台层实际看到什么」画在面板上一眼可见，
    /// 是移植期排错最快的手段；收进设置页后对普通使用没有打扰。
    /// </summary>
    public partial class PanelWindow : Window
    {
        const double PANEL_MARGIN = 20;
        const double SLIVER_DIP = 7;        // 藏起来以后还露在外面的宽度
        const double DOCK_SLOP = 0.34;      // 拖出边界超过窗口这个比例，就认为想贴边
        const int DOCK_ANIM_MS = 220;       // 滑出/滑回动画时长
        const int DOCK_HIDE_DELAY_MS = 420; // 移开鼠标后等这么久才收回去
        const int PREVIEW_HOVER_MS = 500;   // 停多久算「长悬停」，和原版一致
        const double PANEL_W = 340, PANEL_H = 520;
        const double COLLAPSED_H = 44;

        Store _store;
        PanelViewModel _vm;
        CaptureController _capture;
        TextBlock _diag;
        Border _dockGrip, _dockHintBar, _shell;

        // ---- 贴边隐藏状态 ----
        bool _docked;
        string _dockEdge;                  // left / right / top / bottom
        int _dockParkX, _dockParkY;        // 藏起来时的物理坐标
        int _dockOutX, _dockOutY;          // 弹出来时的物理坐标
        bool _dockRevealed;
        DispatcherTimer _dockAnim, _dockHideTimer;
        System.Diagnostics.Stopwatch _dockClock;
        int _dockFromX, _dockFromY, _dockToX, _dockToY, _dockFrames;

        // ---- 悬停预览 ----
        PreviewWindow _preview;
        DispatcherTimer _hoverTimer;
        string _hoverPath;

        /// <summary>诊断视图是否打开。普通属性即可 —— 它不参与数据绑定到 VM。</summary>
        public bool DiagOn { get; private set; }

        public PanelViewModel Vm { get { return _vm; } }

        public PanelWindow()
        {
            Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);
            _diag = this.FindControl<TextBlock>("DiagText");
            _dockGrip = this.FindControl<Border>("DockGrip");
            _dockHintBar = this.FindControl<Border>("DockHintBar");
            _shell = this.FindControl<Border>("Shell");

            _store = new Store(Program.DataDir);
            _vm = new PanelViewModel(_store);
            _vm.ImageRefreshNeeded += OnImageRefreshNeeded;
            // 置顶只改 Z 序，**绝不该动位置**。
            // 上游那个「点置顶跳到左上角」的根因是 SetWindowPos 少传了 SWP_NOMOVE，
            // 位置按传进去的 0,0 生效了。mac 这边由 Avalonia 管 Topmost，
            // 本来就没有这个问题 —— 但仍然显式记一笔位置，并加断言式日志，
            // 免得以后有人换成直接摆窗口时又踩同一个坑。
            _vm.TopmostChanged += on =>
            {
                var before = Position;
                Topmost = on;
                if (Position != before)
                    Program.Log(string.Format("  !! 置顶不应改位置，却从 {0} 变成了 {1}", before, Position));
            };

            _capture = new CaptureController(_store, this);
            _vm.SnipRequested += () => _capture.Trigger("面板 ✂ 按钮");
            _vm.QuitRequested += () => { Program.Platform.UnregisterAllHotkeys(); Close(); };
            _vm.CollapseRequested += ToggleCollapse;
            _vm.OpenDetailRequested += OpenDetail;
            _vm.OpenRecordRequested += OpenRecord;

            DataContext = _vm;

            // 换主题时主动把底色设上去。
            //
            // 为什么不能只靠 {Binding PanelBackground}：PanelBackground 是 VM 的
            // 绑定属性，而 Palette.Apply 改的是**画笔实例的颜色** ——
            // 前者不会因此重算，界面就不会跟着变（实测过：黑主题下面板还是浅色）。
            // 原版同样要靠 Raise("PanelBackground") 才刷新，这里换成直接设，
            // 少一层「谁负责 Raise」的耦合。
            Palette.PaletteApplied += _ => ApplyPanelBackground();
            _vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == "PanelBackground" || e.PropertyName == "PanelAlpha")
                    ApplyPanelBackground();
            };

            Opened += OnOpened;

            // 贴边隐藏：鼠标碰一下滑出来，移开再滑回去
            PointerEntered += (_, __) => { if (_docked) RevealDock(true); };
            PointerExited += (_, __) => { if (_docked) RevealDock(false); };
            // 只要窗口在动（被拖走），先把预览收掉，免得它跟着乱飘
            PositionChanged += (_, __) => HidePreview();
        }

        // ============================================================ 生命周期

        /// <summary>
        /// 把面板底色设到外壳上：主题给的底色 + 当前的透明度。
        /// 注意**条目卡片不受影响** —— 卡片永远不透明，这是设计原则第 5 条。
        /// </summary>
        void ApplyPanelBackground()
        {
            try
            {
                var shell = _shell;
                if (shell == null) return;
                var c = Palette.Current.Panel;
                byte a = (byte)Math.Round(_vm.PanelAlpha * 255);
                shell.Background = new Avalonia.Media.SolidColorBrush(
                    Avalonia.Media.Color.FromArgb(a, c.R, c.G, c.B));
            }
            catch (Exception ex) { Program.Log("  !! 设置面板底色失败：" + ex.Message); }
        }

        void OnOpened(object sender, EventArgs e)
        {
            PositionBottomRight();
            ApplyPanelBackground();
            ApplyStartView();
            _vm.Refresh();
            ApplyPanelBackground();   // Refresh 会应用主题，之后把底色落实
            Topmost = _vm.AlwaysOnTop;
            UpdateDiag();
            _capture.RegisterHotkeys();

            Program.Log("  [t=" + Program.Boot.ElapsedMilliseconds + "ms] 面板已显示");
            Program.Log("    位置(物理) = " + Position + " 尺寸(逻辑) = " + Width + "x" + Height);
            Program.Log("    RenderScaling = " + RenderScaling);
            LogScreens();
        }

        /// <summary>--view=main|bin|done|set 启动时直接进某一页（与 Windows 版同名参数）。</summary>
        void ApplyStartView()
        {
            var v = Program.StartView;
            if (string.IsNullOrEmpty(v) || v == "main") return;
            _vm.View = v;
            Program.Log("  启动视图 = " + v);
        }

        /// <summary>
        /// 放到主屏右下角。
        ///
        /// 注意换算：Avalonia 的 Window.Position 要**物理像素**，
        /// 而 Screens 给的 Bounds 是**逻辑像素** —— 混用会让窗口跑到屏幕外或错位。
        /// </summary>
        void PositionBottomRight()
        {
            var screens = Screens;
            if (screens == null) return;

            var screen = screens.Primary ?? (screens.All.Count > 0 ? screens.All[0] : null);
            if (screen == null) return;

            double s = screen.Scaling > 0 ? screen.Scaling : 1.0;
            var wa = screen.WorkingArea;    // 逻辑像素

            double wPhys = Width * s;
            double hPhys = Height * s;
            double xPhys = wa.X * s + wa.Width * s - wPhys - PANEL_MARGIN * s;
            double yPhys = wa.Y * s + wa.Height * s - hPhys - PANEL_MARGIN * s;

            Position = new Avalonia.PixelPoint((int)Math.Round(xPhys), (int)Math.Round(yPhys));
        }

        // ============================================================ 交互

        /// <summary>顶栏拖动 = 移动窗口（无边框窗口没有系统标题栏可拖）。</summary>
        void OnHeaderDrag(object sender, PointerPressedEventArgs e)
        {
            var p = e.GetCurrentPoint(this);
            if (!p.Properties.IsLeftButtonPressed) return;
            // 拖到顶栏的按钮上时不要抢走点击
            if (e.Source is Button) return;

            HidePreview();
            // 已经在贴边状态又被拖走：先把状态清掉，拖完重新判定
            if (_docked) Undock();

            try
            {
                BeginMoveDrag(e);
                // BeginMoveDrag 是**阻塞**的，返回时拖动已经结束 ——
                // 所以判定贴边就写在这里，而不是另挂一个松手事件。
                MaybeDock();
            }
            catch (Exception ex) { Program.Log("  拖动失败：" + ex.Message); }
        }

        /// <summary>鼠标进入卡片/磁贴：起一个 0.5s 计时器，停了就弹放大预览。</summary>
        void OnCardHoverIn(object sender, PointerEventArgs e)
        {
            var c = sender as Control;
            if (c == null) return;

            // 取这条目的图片绝对路径。两种 VM 都有 ImagePath 字段。
            string path = null;
            var t = c.DataContext as TodoVm;
            if (t != null) path = t.ImagePath;
            else
            {
                var w = c.DataContext as WantVm;
                if (w != null) path = w.ImagePath;
                else
                {
                    // 纸篓/完成列表的卡片也有图，也该能悬停预览
                    var b = c.DataContext as BinVm;
                    if (b != null) path = b.ImagePath;
                    else
                    {
                        var d = c.DataContext as DoneVm;
                        if (d != null) path = d.ImagePath;
                    }
                }
            }
            if (string.IsNullOrEmpty(path)) return;   // 没图的条目不弹预览

            StartHoverPreview(c, path);
        }

        void OnCardHoverOut(object sender, PointerEventArgs e) { HidePreview(); }

        void OnTodoCardClick(object sender, PointerReleasedEventArgs e)
        {
            var vm = (sender as Control)?.DataContext as TodoVm;
            if (vm == null) return;
            _vm.OpenCmd.Execute(vm.Id);
        }

        void OnWantTileClick(object sender, PointerReleasedEventArgs e)
        {
            var vm = (sender as Control)?.DataContext as WantVm;
            if (vm == null) return;
            _vm.OpenCmd.Execute(vm.Id);
        }

        void OnBinCardClick(object sender, PointerReleasedEventArgs e)
        {
            var vm = (sender as Control)?.DataContext as BinVm;
            if (vm == null) return;

            // v0.9.1：只有「初心 Wantdo」能捡回来。
            // 「使命 Todo」是你主动舍弃的，点它不该有任何动作 ——
            // 那一区的唯一出口是顶部的「倒垃圾」（真删除）。
            if (!vm.CanRestore)
            {
                Program.Log("  纸篓：使命 Todo 不能捡回（只能倒垃圾）");
                return;
            }
            _vm.RestoreCmd.Execute(vm.Id);
        }

        void OnDoneCardClick(object sender, PointerReleasedEventArgs e)
        {
            var vm = (sender as Control)?.DataContext as DoneVm;
            if (vm == null) return;
            _vm.OpenDoneCmd.Execute(vm.Id);
        }

        /// <summary>点主题色块换背景颜色。</summary>
        void OnThemeSwatchClick(object sender, PointerReleasedEventArgs e)
        {
            var vm = (sender as Control)?.DataContext as ThemeOptionVm;
            if (vm == null) return;
            _vm.PickThemeCmd.Execute(vm.Key);
            ApplyPanelBackground();
            Program.Log("  背景颜色 -> " + vm.Key);
        }

        /// <summary>
        /// 透明度滑块松手才写盘 —— 和上游 panel.js 的 input/change 分工一致：
        /// 拖动过程中只改画面（预演），松手才落盘，免得拖一次写几十遍文件。
        /// </summary>
        void OnOpacityCommitted(object sender, PointerReleasedEventArgs e)
        {
            _vm.SaveOpacity();
            ApplyPanelBackground();
            Program.Log("  透明度已保存 = " + _vm.OpacityText);
        }

        /// <summary>衰减两个滑块共用：松手才写盘。</summary>
        void OnDecayCommitted(object sender, PointerReleasedEventArgs e)
        {
            _vm.SaveDecay();
            Program.Log(string.Format("  衰减设置已保存：保护期 {0} / 放手期 {1}",
                _vm.FadeText, _vm.BinText));
        }

        void OnSaveDecay(object sender, RoutedEventArgs e)
        {
            _vm.SaveDecay();
            _vm.SaveOpacity();
            Program.Log("  衰减/透明度设置已保存");
        }

        void OnDiagToggle(object sender, RoutedEventArgs e)
        {
            DiagOn = !DiagOn;
            UpdateDiag();
        }

        /// <summary>自检用：把折叠切换暴露出来（正常路径是顶栏那个 ▾ 按钮）。</summary>
        public void ToggleCollapseForTest() { ToggleCollapse(); }

        void ToggleCollapse()
        {
            _vm.Collapsed = !_vm.Collapsed;
            HidePreview();
            // 贴边藏着的时候先弹回来，不然收起来就找不着了（原版同样处理）
            if (_vm.Collapsed) Undock();

            var before = Position;

            // 折叠成一条：只留顶栏。
            Height = _vm.Collapsed ? COLLAPSED_H : PANEL_H;

            // 原地缩放：只改尺寸，位置一个像素都不动。
            //
            // 之前这里调的是 PositionBottomRight() —— 于是每次折叠/展开都把面板
            // 甩回右下角，用户拖到别处再折叠就被拽回去了。
            // 这正是上游 v0.9.1 修的那个「点折叠甩到右下角」的同款缺陷
            // （他们在 Windows 上是无条件写 Left/Top）。
            ClampIntoWorkArea();

            Program.Log(string.Format("  折叠 = {0}：位置 {1} -> {2}，高度 {3}",
                _vm.Collapsed, before, Position, Height));
        }

        /// <summary>
        /// 把窗口拉回它**当前所在那块屏**的工作区里。
        ///
        /// 尺寸变化后可能超出屏幕边界，但位置本身不该被改动 ——
        /// 只做「拉回来」，不做「摆到某处」。这两件事混在一起正是那个 bug 的成因。
        /// </summary>
        void ClampIntoWorkArea()
        {
            Avalonia.Rect work, wr;
            if (!WindowWorkArea(out work, out wr)) return;

            double w = wr.Width, h = wr.Height;
            if (w <= 0 || h <= 0) return;

            double x = Clamp(wr.X, work.X, work.Right - w);
            double y = Clamp(wr.Y, work.Y, work.Bottom - h);
            if (Math.Abs(x - wr.X) > 0.5 || Math.Abs(y - wr.Y) > 0.5)
            {
                Program.Log(string.Format("  尺寸变了超出屏幕，拉回 ({0},{1}) -> ({2:0},{3:0})",
                    wr.X, wr.Y, x, y));
                MoveTo((int)Math.Round(x), (int)Math.Round(y));
            }
        }

        /// <summary>缩略图解码完成后刷新那几项 —— 不改任何业务状态，只让界面重画。</summary>
        void OnImageRefreshNeeded()
        {
            // VM 属性本身已经 Raise 过，这里只需要在 UI 线程上让绑定重新求值。
            // Dispatcher.Post 是幂等且便宜的，重复调用没有副作用。
            Dispatcher.UIThread.Post(() =>
            {
                // 触发一次集合级刷新：ItemsControl 会重新读取每一项的 Thumb
                var t = _vm.Todos; int a = t.Count;
                var w = _vm.Wants; int b = w.Count;
            });
        }

        // ============================================================ 打开子窗口

        readonly System.Collections.Generic.List<Window> _kids = new System.Collections.Generic.List<Window>();

        /// <summary>
        /// 打开详情窗。详情窗里对 Wantdo 点 Get Done 会请求打开完成记录窗 ——
        /// 那条链路（先写心得，再算完成）就是在这里接起来的。
        /// </summary>
        void OpenDetail(string id)
        {
            var d = new DetailWindow(_store, id);
            d.RecordRequested += rid => OpenRecord(rid);
            Track(d);
            d.Show();
            Program.Log("  [面板] 打开详情窗：" + id);
        }

        /// <summary>
        /// 打开完成记录窗。
        /// mode 判断与原版一致：已完成的看回看（view），未完成的看写心得（write）；
        /// write 模式下点 Get Done 才真正落 Done。
        /// </summary>
        void OpenRecord(string id)
        {
            var it = _store.Get(id);
            bool done = it != null && J.Str(it, "state", "") == "done";
            var w = new RecordWindow(_store, id, done ? "view" : "write");

            w.Committed += (rid, reflection) =>
            {
                // 写心得点 Get Done：带着心得完成，然后收进完成列表（file=true）
                _store.Done(rid, reflection != null && reflection.Length > 0, reflection, true);
                Program.Log("  [面板] 已带着心得完成：" + rid);
                _vm.Refresh();
            };

            Track(w);
            w.Show();
            Program.Log("  [面板] 打开完成记录窗：" + id + "（" + (done ? "view" : "write") + "）");
        }

        void Track(Window w)
        {
            _kids.Add(w);
            w.Closed += (_, __) => { _kids.Remove(w); _vm.Refresh(); };
        }


        // ============================================================ 贴边隐藏

        /// <summary>
        /// 窗口当前所在那块屏的工作区，物理像素。
        /// 用窗口中心点去问，比用左上角靠谱 —— 窗口一半在屏外时左上角可能已经是负的。
        /// </summary>
        bool WindowWorkArea(out Avalonia.Rect work, out Avalonia.Rect wr)
        {
            work = default(Avalonia.Rect);
            wr = default(Avalonia.Rect);

            var screens = Screens;
            if (screens == null) return false;

            double w = Width, h = Height;
            double cx = Position.X + w / 2, cy = Position.Y + h / 2;
            var screen = screens.ScreenFromPoint(new Avalonia.PixelPoint((int)cx, (int)cy))
                         ?? screens.Primary;
            if (screen == null) return false;

            double sc = screen.Scaling > 0 ? screen.Scaling : 1.0;
            var wa = screen.WorkingArea;
            // 统一成物理像素（窗口 Position 是物理像素）
            work = new Avalonia.Rect(wa.X * sc, wa.Y * sc, wa.Width * sc, wa.Height * sc);
            wr = new Avalonia.Rect(Position.X, Position.Y, w * sc, h * sc);
            return true;
        }

        /// <summary>
        /// 拖完松手看看：窗口被推出屏幕边界超过一定比例，就贴到那条边上去，
        /// 只留一条窄边在外面；鼠标碰一下就滑出来，移开再滑回去。
        /// </summary>
        void MaybeDock()
        {
            if (_vm.Collapsed) return;

            Avalonia.Rect work, wr;
            if (!WindowWorkArea(out work, out wr)) return;
            double w = wr.Width, h = wr.Height;
            if (w <= 0 || h <= 0) return;

            string edge = null;
            if (wr.X < work.X - w * DOCK_SLOP) edge = "left";
            else if (wr.Right > work.Right + w * DOCK_SLOP) edge = "right";
            else if (wr.Y < work.Y - h * DOCK_SLOP) edge = "top";
            else if (wr.Bottom > work.Bottom + h * DOCK_SLOP) edge = "bottom";

            if (edge == null) { Undock(); return; }
            Dock(edge, work, wr, w, h);
        }

        void Dock(string edge, Avalonia.Rect work, Avalonia.Rect wr, double w, double h)
        {
            double sc = RenderScaling > 0 ? RenderScaling : 1.0;
            double sliver = SLIVER_DIP * sc;

            // 弹出来的位置：整块可见，并且在那条边上对齐
            double outX, outY;
            if (edge == "left") { outX = work.X; outY = Clamp(wr.Y, work.Y, work.Bottom - h); }
            else if (edge == "right") { outX = work.Right - w; outY = Clamp(wr.Y, work.Y, work.Bottom - h); }
            else if (edge == "top") { outX = Clamp(wr.X, work.X, work.Right - w); outY = work.Y; }
            else { outX = Clamp(wr.X, work.X, work.Right - w); outY = work.Bottom - h; }

            double parkX = edge == "left" ? work.X - w + sliver
                         : edge == "right" ? work.Right - sliver
                         : outX;
            double parkY = edge == "top" ? work.Y - h + sliver
                         : edge == "bottom" ? work.Bottom - sliver
                         : outY;

            _docked = true;
            _dockEdge = edge;
            _dockOutX = (int)Math.Round(outX); _dockOutY = (int)Math.Round(outY);
            _dockParkX = (int)Math.Round(parkX); _dockParkY = (int)Math.Round(parkY);
            _dockRevealed = false;
            StopDockAnim();
            MoveTo(_dockParkX, _dockParkY);

            _vm.IsDocked = true;
            _vm.DockHint = "贴边隐藏中 · 鼠标移上去滑出来 · 拖回屏幕中间取消";
            ShowGrip(edge, true);
            if (_dockHintBar != null) _dockHintBar.IsVisible = false;

            Program.Log(string.Format("  贴边隐藏：{0} 藏到 ({1},{2})，弹出位置 ({3},{4})",
                edge, _dockParkX, _dockParkY, _dockOutX, _dockOutY));
        }

        /// <summary>
        /// 把那条紫色抓手摆到**露在外面**的那条边上。
        /// 注意是反的：靠右隐藏时窗口停在 work.Right - sliver，露出来的是窗口的**左**边。
        /// </summary>
        void ShowGrip(string edge, bool on)
        {
            if (_dockGrip == null) return;
            if (!on) { _dockGrip.IsVisible = false; return; }

            if (edge == "left" || edge == "right")
            {
                _dockGrip.Width = 5; _dockGrip.Height = 46;
                _dockGrip.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
                // 靠左隐藏 -> 露出右边；靠右隐藏 -> 露出左边
                _dockGrip.HorizontalAlignment = edge == "left"
                    ? Avalonia.Layout.HorizontalAlignment.Right
                    : Avalonia.Layout.HorizontalAlignment.Left;
                _dockGrip.Margin = edge == "left" ? new Thickness(0, 0, 1, 0) : new Thickness(1, 0, 0, 0);
            }
            else
            {
                _dockGrip.Width = 46; _dockGrip.Height = 5;
                _dockGrip.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center;
                _dockGrip.VerticalAlignment = edge == "top"
                    ? Avalonia.Layout.VerticalAlignment.Bottom
                    : Avalonia.Layout.VerticalAlignment.Top;
                _dockGrip.Margin = edge == "top" ? new Thickness(0, 0, 0, 1) : new Thickness(0, 1, 0, 0);
            }
            _dockGrip.IsVisible = true;
        }

        static double Clamp(double v, double lo, double hi)
        {
            return hi < lo ? lo : Math.Max(lo, Math.Min(v, hi));
        }

        void Undock()
        {
            if (!_docked) return;
            _docked = false;
            StopDockAnim();
            if (_dockHideTimer != null) { _dockHideTimer.Stop(); _dockHideTimer = null; }
            _vm.IsDocked = false;
            _vm.DockHint = null;
            if (_dockGrip != null) _dockGrip.IsVisible = false;
            if (_dockHintBar != null) _dockHintBar.IsVisible = false;
            _dockEdge = null;
            _dockRevealed = false;
        }

        /// <summary>滑出 / 滑回。</summary>
        void RevealDock(bool on)
        {
            if (!_docked || _dockRevealed == on) return;
            _dockRevealed = on;
            if (_dockGrip != null) _dockGrip.IsVisible = !on;
            if (_dockHintBar != null) _dockHintBar.IsVisible = on;

            // 收回去的时候先等一小会儿：鼠标刚离开边缘时容易立刻又碰到那条窄边，
            // 不等一下就会来回抖（原版也是这么处理的）
            if (!on)
            {
                if (_dockHideTimer != null) _dockHideTimer.Stop();
                _dockHideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(DOCK_HIDE_DELAY_MS) };
                _dockHideTimer.Tick += (s2, e2) =>
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

        /// <summary>
        /// 滑动动画。
        ///
        /// 与原版的一处差异：原版挂在 WPF 的 CompositionTarget.Rendering 上，
        /// 每渲染帧更新一次位置、和 vsync 对齐；Avalonia 没有完全等价的公开 API，
        /// 这里用 16ms 的 DispatcherTimer 近似（约 60fps）。
        /// 代价是位置更新与渲染节奏可能错开、极端情况下会略微不匀，
        /// 但滑动幅度只有几百像素、时长 220ms，肉眼看不出差别 ——
        /// 换来的是不依赖框架内部 API。
        /// </summary>
        void StartDockAnim(int toX, int toY)
        {
            StopDockAnim();

            _dockFromX = Position.X; _dockFromY = Position.Y;
            _dockToX = toX; _dockToY = toY;
            if (_dockFromX == _dockToX && _dockFromY == _dockToY) return;

            _dockClock = System.Diagnostics.Stopwatch.StartNew();
            _dockFrames = 0;

            _dockAnim = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            _dockAnim.Tick += (s, e) =>
            {
                double ms = _dockClock.Elapsed.TotalMilliseconds;
                double t = Math.Min(1.0, ms / DOCK_ANIM_MS);
                double k = 1 - Math.Pow(1 - t, 3);   // ease-out cubic
                MoveTo(
                    (int)Math.Round(_dockFromX + (_dockToX - _dockFromX) * k),
                    (int)Math.Round(_dockFromY + (_dockToY - _dockFromY) * k));
                _dockFrames++;
                if (t >= 1.0)
                {
                    StopDockAnim();
                    MoveTo(_dockToX, _dockToY);   // 收尾对齐，别停在中间
                    Program.Log(string.Format("  滑动完成：{0} 帧 / {1:0}ms（{2:0} fps）",
                        _dockFrames, ms, _dockFrames * 1000.0 / Math.Max(1, ms)));
                }
            };
            _dockAnim.Start();
        }

        void StopDockAnim()
        {
            if (_dockAnim == null) return;
            _dockAnim.Stop();
            _dockAnim = null;
        }

        void MoveTo(int x, int y)
        {
            Position = new Avalonia.PixelPoint(x, y);
        }

        // ============================================================ 长悬停放大预览

        /// <summary>
        /// 在一个条目上停约 0.5 秒，旁边弹出放大预览 —— 不用点进去就能看清图里记了什么。
        /// 只在有图时弹（没图的条目预览没有意义）。
        /// </summary>
        void StartHoverPreview(Control card, string imagePath)
        {
            if (string.IsNullOrEmpty(imagePath)) return;
            HidePreview();
            _hoverPath = imagePath;

            _hoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(PREVIEW_HOVER_MS) };
            _hoverTimer.Tick += (s, e) =>
            {
                _hoverTimer.Stop();
                _hoverTimer = null;
                ShowPreview(card, _hoverPath);
            };
            _hoverTimer.Start();
        }

        void HidePreview()
        {
            if (_hoverTimer != null) { _hoverTimer.Stop(); _hoverTimer = null; }
            _hoverPath = null;
            if (_preview != null)
            {
                try { _preview.Close(); } catch (Exception) { }
                _preview = null;
            }
        }

        void ShowPreview(Control card, string imagePath)
        {
            try
            {
                var img = Images.Get(imagePath, 440);
                if (img == null) return;

                if (_preview == null) _preview = new PreviewWindow();

                // 锚点：卡片在屏幕上的位置。卡片坐标是相对面板的布局单位，
                // 要换算成物理像素才能和窗口的物理坐标对齐。
                double sc = RenderScaling > 0 ? RenderScaling : 1.0;
                var topLeft = card.TranslatePoint(new Avalonia.Point(0, 0), this);
                if (topLeft == null) return;

                double anchorTop = Position.Y + topLeft.Value.Y * sc;
                double panelLeft = Position.X;
                double panelW = Width * sc;

                _preview.ShowAt(img, anchorTop, card.Bounds.Height * sc, panelLeft, panelW);
                Program.Log("  放大预览已弹出：" + imagePath);
            }
            catch (Exception ex)
            {
                Program.Log("  放大预览失败：" + ex.GetType().Name + " " + ex.Message);
            }
        }

        // ============================================================ 贴边自检

        /// <summary>
        /// 验收用：把窗口挪到屏幕外再判定贴边，并把结果写进日志。
        ///
        /// 为什么要做成自检：贴边隐藏需要人真的把窗口拖出屏幕边缘才触发，
        /// 而这几行几何换算（露出的窄边、弹出位置、抓手该贴哪一边）恰恰是最容易算错的，
        /// 错了以后表现为「拖出去就再也找不回来」。做成命令就不用在验收时反复手拖。
        /// </summary>
        public void DockSelfTest()
        {
            Program.Log("=== 贴边隐藏自检 ===");

            Avalonia.Rect work, wr;
            if (!WindowWorkArea(out work, out wr))
            {
                Program.Log("!! 拿不到工作区，无法自检");
                return;
            }
            Program.Log(string.Format("  工作区(物理) = {0}　窗口 = {1}", work, wr));

            // 分别把窗口挪到四条边外面，逐一判定。
            // ⚠️ 每个方向都要**先从屏幕中间重新出发**：否则上一个方向留下的坐标
            // 会污染下一个判定（第一版就踩了：测 bottom 时窗口还停在左边屏外，
            // 于是判定成了 left，自检报了个假结果）。
            foreach (var edge in new[] { "right", "left", "bottom", "top" })
            {
                double x = work.X + (work.Width - wr.Width) / 2;
                double y = work.Y + (work.Height - wr.Height) / 2;
                switch (edge)
                {
                    case "right": x = work.Right + 50; break;
                    case "left": x = work.X - wr.Width - 50; break;
                    case "bottom": y = work.Bottom + 50; break;
                    case "top": y = work.Y - wr.Height - 50; break;
                }
                MoveTo((int)Math.Round(x), (int)Math.Round(y));
                MaybeDock();

                if (_docked)
                {
                    // 抓手必须真的落在**屏内**，否则会出现「拖出去就再也找不回来」。
                    // 靠右/靠下隐藏时窗口把左侧（或上侧）留在屏内，露的是窗口左/上那一条；
                    // 靠左/靠上隐藏时反过来。这条断言是补自检时新加的。
                    bool gripOnScreen;
                    if (_dockEdge == "right" || _dockEdge == "bottom")
                        gripOnScreen = _dockParkX < work.Right && _dockParkY < work.Bottom;
                    else
                        gripOnScreen = _dockParkX + wr.Width > work.X && _dockParkY + wr.Height > work.Y;

                    Program.Log(string.Format(
                        "  判定 {0,-6} → 已贴边；藏到 ({1},{2})，可弹出到 ({3},{4})，抓手可见={5}，抓手在屏内={6}",
                        edge, _dockParkX, _dockParkY, _dockOutX, _dockOutY,
                        _dockGrip != null && _dockGrip.IsVisible, gripOnScreen));
                    Undock();
                }
                else
                {
                    Program.Log(string.Format("  判定 {0,-6} → 未贴边（不符合预期）", edge));
                }
            }

            // 再验一次「拖回屏幕中间应当取消贴边」
            MoveTo((int)Math.Round(work.X + 100), (int)Math.Round(work.Y + 100));
            MaybeDock();
            Program.Log(string.Format("  拖回屏幕中间 → 贴边状态 = {0}（应为 False）", _docked));

            // 收尾：放回右下角
            PositionBottomRight();
            Program.Log(string.Format("  已复位到右下角 = {0}", Position));
            Program.Log("=== 贴边自检结束 ===");
        }

        // ============================================================ 诊断

        void UpdateDiag()
        {
            if (_diag == null) return;
            if (!DiagOn) { _diag.Text = ""; return; }

            var sb = new StringBuilder();
            try
            {
                sb.AppendLine("平台层: " + Program.Platform.Name);
                sb.AppendLine("Avalonia: " + typeof(Window).Assembly.GetName().Version);
                sb.AppendLine();

                var screens = Screens;
                if (screens != null)
                {
                    sb.AppendLine("屏幕数: " + screens.All.Count);
                    foreach (var s in screens.All)
                    {
                        sb.AppendLine(string.Format("  scaling={0} bounds(逻辑)={1}",
                            s.Scaling, s.Bounds));
                        sb.AppendLine(string.Format("  work(逻辑)={0}", s.WorkingArea));
                    }
                }
                sb.AppendLine();

                sb.AppendLine("本窗口:");
                sb.AppendLine("  RenderScaling = " + RenderScaling);
                sb.AppendLine("  Position(物理) = " + Position);
                sb.AppendLine("  尺寸(逻辑) = " + Width + " x " + Height);
                sb.AppendLine();

                sb.AppendLine("光标(物理) = " + Program.Platform.GetCursorPosition());
                sb.AppendLine();

                sb.AppendLine("权限:");
                sb.AppendLine("  屏幕录制 = " + (Program.Platform.HasScreenCapturePermission() ? "已授权" : "未授权"));
                sb.AppendLine("  辅助功能 = " + (Program.Platform.HasAccessibilityPermission() ? "已授权" : "未授权"));
                sb.AppendLine();

                sb.AppendLine("数据目录:");
                sb.AppendLine("  " + Program.DataDir);
                sb.AppendLine();
                sb.AppendLine("数据: Todo " + _vm.Todos.Count + " / Wantdo " + _vm.Wants.Count
                    + " / 纸篓 " + _vm.Bins.Count + " / 完成组 " + _vm.DoneGroups.Count);
            }
            catch (Exception ex)
            {
                sb.AppendLine();
                sb.AppendLine("!! 诊断失败: " + ex.GetType().Name + " " + ex.Message);
            }

            _diag.Text = sb.ToString();
        }

        void LogScreens()
        {
            var screens = Screens;
            if (screens == null) { Program.Log("    !! Screens 为 null"); return; }
            Program.Log("    Avalonia 屏幕数 = " + screens.All.Count);
            foreach (var s in screens.All)
            {
                Program.Log(string.Format("      scaling={0} bounds(逻辑)={1} work(逻辑)={2}",
                    s.Scaling, s.Bounds, s.WorkingArea));
                Program.Log(string.Format("      bounds(物理) = {0}x{1} @ ({2},{3})",
                    s.Bounds.Width * s.Scaling, s.Bounds.Height * s.Scaling,
                    s.Bounds.X * s.Scaling, s.Bounds.Y * s.Scaling));
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            Program.Platform.UnregisterAllHotkeys();
            // 子窗口是独立顶层窗，主窗关掉不会带走它们 —— 不显式关会留下孤儿窗口
            foreach (var w in _kids.ToArray())
            {
                try { w.Close(); } catch (Exception) { }
            }
            _kids.Clear();
            base.OnClosed(e);
        }
    }
}
