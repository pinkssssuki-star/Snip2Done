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

        Store _store;
        PanelViewModel _vm;
        TextBlock _diag;

        /// <summary>诊断视图是否打开。普通属性即可 —— 它不参与数据绑定到 VM。</summary>
        public bool DiagOn { get; private set; }

        public PanelViewModel Vm { get { return _vm; } }

        public PanelWindow()
        {
            Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);
            _diag = this.FindControl<TextBlock>("DiagText");

            _store = new Store(Program.DataDir);
            _vm = new PanelViewModel(_store);
            _vm.ImageRefreshNeeded += OnImageRefreshNeeded;
            _vm.TopmostChanged += on => Topmost = on;

            _vm.SnipRequested += () => Program.Log("  [面板] 请求截图（P3 待接热键与归类窗）");
            _vm.QuitRequested += () => { Program.Platform.UnregisterAllHotkeys(); Close(); };
            _vm.CollapseRequested += ToggleCollapse;
            _vm.OpenDetailRequested += id => Program.Log("  [面板] 打开详情：" + id + "（P4 待做）");
            _vm.OpenRecordRequested += id => Program.Log("  [面板] 打开完成记录：" + id + "（P4 待做）");

            DataContext = _vm;
            Opened += OnOpened;
        }

        // ============================================================ 生命周期

        void OnOpened(object sender, EventArgs e)
        {
            PositionBottomRight();
            ApplyStartView();
            _vm.Refresh();
            Topmost = _vm.AlwaysOnTop;
            UpdateDiag();

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
            try { BeginMoveDrag(e); }
            catch (Exception ex) { Program.Log("  拖动失败：" + ex.Message); }
        }

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
            // 点纸篓里的卡片 = 捞回来（和面板上的 ↩ 同一个动作）
            _vm.RestoreCmd.Execute(vm.Id);
        }

        void OnDoneCardClick(object sender, PointerReleasedEventArgs e)
        {
            var vm = (sender as Control)?.DataContext as DoneVm;
            if (vm == null) return;
            _vm.OpenDoneCmd.Execute(vm.Id);
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

        void ToggleCollapse()
        {
            _vm.Collapsed = !_vm.Collapsed;
            // 折叠成一条：只留顶栏。高度是「量着顶栏实际需要」定的，
            // 不会把按钮挤变形（原版的做法，这里按顶栏实际高度取）。
            Height = _vm.Collapsed ? 44 : 500;
            PositionBottomRight();
            Program.Log("  折叠 = " + _vm.Collapsed + "，高度 = " + Height);
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
            base.OnClosed(e);
        }
    }
}
