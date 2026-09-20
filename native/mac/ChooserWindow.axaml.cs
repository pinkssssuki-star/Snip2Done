using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace RemeberEr
{
    /// <summary>
    /// 截图后的归类窗：「这一张，是 Todo 还是 Wantdo？」
    ///
    /// 尺寸与交互照着原版搬：312×212、Esc 算了、1/2/3 直接选、
    /// 标题留空则回落到截图时的窗口标题、右键取消。
    ///
    /// 摆放规则也是原版那条：贴着选区右下角外侧，越界就往里挪，
    /// 但**钳制范围是截图那块屏**，不是主屏 ——
    /// 原版在这一点上踩过坑（用主屏工作区钳制，结果副屏截完图归类窗被拽回主屏）。
    /// </summary>
    public partial class ChooserWindow : Window
    {
        /// <summary>归类窗要展示与落库的东西。</summary>
        public class Payload
        {
            /// <summary>整张截图的 PNG（落库用）。</summary>
            public byte[] Png;
            /// <summary>256px 宽的缩略图 PNG，落库时一并写到 images/&lt;id&gt;.thumb.png。</summary>
            public byte[] ThumbPng;
            /// <summary>预览位图。</summary>
            public Bitmap Preview;
            /// <summary>来源应用名。</summary>
            public string App;
            /// <summary>窗口标题。</summary>
            public string Title;
            /// <summary>
            /// 手动记一笔模式：不显示缩略图，并**藏掉 Wantdo 按钮**。
            /// 因为手动记的 Todo 必须有期限，而「无期限」在 Todo 语境下是自相矛盾的 ——
            /// 这是在给 Todo 选时间，不是在问「这是 Todo 还是 Wantdo」。
            /// </summary>
            public bool Manual;
        }

        readonly Payload _p;
        readonly DisplayInfo _display;
        readonly int _selX, _selY, _selW, _selH;

        Image _thumb;
        TextBlock _srcText;
        TextBox _titleBox, _daysBox;

        /// <summary>choice, typedTitle, days。「两三天」那一档的自定义天数，0 = 没写。</summary>
        public event Action<string, string, int> Decided;

        public ChooserWindow(Payload p, DisplayInfo display,
                             int selX, int selY, int selW, int selH)
        {
            Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);

            _p = p;
            _display = display;
            _selX = selX; _selY = selY; _selW = selW; _selH = selH;

            _thumb = this.FindControl<Image>("Thumb");
            _srcText = this.FindControl<TextBlock>("SrcText");
            _titleBox = this.FindControl<TextBox>("TitleBox");
            _daysBox = this.FindControl<TextBox>("DaysBox");

            if (p.Preview != null) _thumb.Source = p.Preview;

            // 手动模式：藏掉 Wantdo 那一列，并把它的列宽和前面的间隔列一起收成 0，
            // 剩下两个按钮自动撑满（和上游同样的做法）。
            Program.Log(string.Format("  [归类窗] 收到 payload hash={0} Manual={1} PickWant={2} ThumbBox={3}",
                p.GetHashCode(), p.Manual,
                this.FindControl<Button>("PickWant") != null,
                this.FindControl<Border>("ThumbBox") != null));
            if (p.Manual)
            {
                var want = this.FindControl<Button>("PickWant");
                var thumbBox = this.FindControl<Border>("ThumbBox");
                var titleBox = this.FindControl<TextBox>("TitleBox");
                if (want != null) want.IsVisible = false;
                if (thumbBox != null) thumbBox.IsVisible = false;
                // 标题框也藏掉：手动模式的标题就是输入框里刚打的那句话，
                // 再问一次是重复的（上游同样是 !Manual 才 Focus/SelectAll）。
                if (titleBox != null) titleBox.IsVisible = false;

                // 列宽要从 Grid 的 ColumnDefinitions 里取 —— FindControl 只找控件，
                // 找不到 ColumnDefinition（它不是 Control）。
                var pickRow = this.FindControl<Grid>("PickRow");
                if (pickRow != null && pickRow.ColumnDefinitions.Count >= 5)
                {
                    pickRow.ColumnDefinitions[1].Width = new GridLength(0);   // 间隔列
                    pickRow.ColumnDefinitions[3].Width = new GridLength(0);   // 间隔列
                    pickRow.ColumnDefinitions[4].Width = new GridLength(0);   // Wantdo 列
                }
            }
            BuildSourceLine(p.App, p.Title);
            _titleBox.Text = p.Title ?? "";

            PositionNearSelection();

            KeyDown += OnKeyDown;
            Opened += (_, __) =>
            {
                _titleBox.Focus();
                _titleBox.SelectAll();
                Program.Log(string.Format("  归类窗已显示，位置(物理) = {0}", Position));
                if (Program.AutoChoose != null) ScheduleAutoPick();
            };
        }

        /// <summary>
        /// 贴着选区右下角外侧放，越界就往里挪；钳制范围是**截图那块屏**。
        ///
        /// 注意 DisplayInfo 里统一是**物理像素**（见 IPlatform 的约定），
        /// 而 Avalonia 的 Position 也要物理像素 —— 两边口径一致，这里不需要再折算。
        /// 逻辑尺寸（312×212）只有在设置 Width/Height 时才用得上。
        /// </summary>
        void PositionNearSelection()
        {
            double s = _display.Scale > 0 ? _display.Scale : 1.0;
            double physW = Math.Round(Width * s);
            double physH = Math.Round(Height * s);

            double monL = _display.X, monT = _display.Y;
            double monR = _display.X + _display.W, monB = _display.Y + _display.H;

            // 选区右下角，往下 10 物理像素
            double x = monL + _selX + _selW - physW;
            double y = monT + _selY + _selH + 10;

            double minX = monL + 8, maxX = monR - physW - 8;
            double minY = monT + 8, maxY = monB - physH - 8;

            // 下面放不下就翻到选区上方
            if (y > maxY) y = monT + _selY - physH - 10;

            double px = Math.Max(minX, Math.Min(x, Math.Max(minX, maxX)));
            double py = Math.Max(minY, Math.Min(y, Math.Max(minY, maxY)));

            Position = new Avalonia.PixelPoint((int)Math.Round(px), (int)Math.Round(py));

            Program.Log(string.Format(
                "  归类窗摆到物理 ({0:0},{1:0}) {2:0}x{3:0}（屏 {4},{5}-{6},{7} 缩放 {8:0.##}）",
                px, py, physW, physH, monL, monT, monR, monB, s));
        }

        void BuildSourceLine(string app, string title)
        {
            if (string.IsNullOrEmpty(app) && string.IsNullOrEmpty(title))
            {
                _srcText.Text = "（没拿到来源信息）";
                return;
            }
            // Avalonia 的 TextBlock 没有 WPF 那种 Inlines.Add(Run) 的混色写法那么方便，
            // 这里用两个 TextBlock 拼：来源用灰色，标题用深色。
            _srcText.Text = string.IsNullOrEmpty(app) ? title : (app + " · " + title);
        }

        // ============================================================ 选择

        void Pick(string choice)
        {
            Dismiss(choice, (_titleBox.Text ?? "").Trim(), ReadDays());
        }

        /// <summary>
        /// 「几天后」输入框里的数字。
        /// 空着或写了 0 → 返回 0，交给数据层在 48~72 小时里随机挑一个。
        /// </summary>
        int ReadDays()
        {
            if (_daysBox == null) return 0;
            string s = (_daysBox.Text ?? "").Trim();
            if (s.Length == 0) return 0;
            int n;
            if (!int.TryParse(s, out n)) return 0;
            return n < 0 ? 0 : (n > 365 ? 365 : n);   // 夹一下，免得填个天文数字
        }

        /// <summary>自动化验收用：不用手点，直接选一个。</summary>
        public void AutoPick(string choice) { Pick(choice); }

        /// <summary>自动化验收用：带一个「几天后」的天数再选。</summary>
        public void AutoPickWithDays(string choice, int days)
        {
            if (_daysBox != null && days > 0) _daysBox.Text = days.ToString();
            Pick(choice);
        }

        void Cancel() { Dismiss(null, null, 0); }

        /// <summary>
        /// 收摊：**先 Hide 再 Close**，并且回调放在 Hide 之后、Close 之前 ——
        /// 回调会去刷新面板，那时窗口得已经不在屏幕上了（原版的顺序）。
        /// </summary>
        void Dismiss(string choice, string typed, int days)
        {
            try { Hide(); } catch (Exception) { }
            var d = Decided;
            if (d != null) d(choice, typed, days);
            Close();
        }

        void OnPickToday(object sender, Avalonia.Interactivity.RoutedEventArgs e) { Pick("today"); }
        void OnPickSoon(object sender, Avalonia.Interactivity.RoutedEventArgs e) { Pick("soon"); }
        void OnPickWant(object sender, Avalonia.Interactivity.RoutedEventArgs e) { Pick("want"); }

        void OnRightDown(object sender, PointerPressedEventArgs e)
        {
            if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
            {
                e.Handled = true;
                Cancel();
            }
        }

        /// <summary>
        /// 「几天后」只收数字 —— 这个框很小，敲进去一个字母就完全看不出填错了。
        /// 直接拦掉非数字字符比事后校验友好。
        /// </summary>
        void OnDaysInput(object sender, TextInputEventArgs e)
        {
            foreach (var ch in e.Text ?? "")
            {
                if (ch < '0' || ch > '9') { e.Handled = true; return; }
            }
        }

        /// <summary>在「几天后」框里按 Enter 也是确认（和主输入框一致）。</summary>
        void OnDaysKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { e.Handled = true; Pick("soon"); }
        }

        void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) { e.Handled = true; Cancel(); return; }

            // 只有焦点在标题框里打字时才让数字键进输入框，否则 1/2/3 直接选。
            // Avalonia 的取法是 TopLevel.FocusManager.GetFocusedElement()，
            // 没有 WPF 那种静态的 Keyboard.FocusedElement。
            var fm = TopLevel.GetTopLevel(this)?.FocusManager;
            bool typing = fm?.GetFocusedElement() == _titleBox;

            if (e.Key == Key.Enter)
            {
                // 输入框里回车只是收焦点，避免手一抖就误存
                if (typing) KeyboardNavigationHandler(); // 见下面的说明
                e.Handled = true;
                return;
            }
            if (typing) return;

            if (e.Key == Key.D1 || e.Key == Key.NumPad1) { Pick("today"); e.Handled = true; }
            else if (e.Key == Key.D2 || e.Key == Key.NumPad2) { Pick("soon"); e.Handled = true; }
            else if (e.Key == Key.D3 || e.Key == Key.NumPad3) { Pick("want"); e.Handled = true; }
        }

        /// <summary>
        /// 「回车只收焦点」的实现。
        /// WPF 有 Keyboard.ClearFocus()，Avalonia 没有对应 API，
        /// 于是把焦点移到窗口本身 —— 之后数字键就会走「直接选」那条分支。
        /// </summary>
        void KeyboardNavigationHandler()
        {
            try { Focus(); } catch (Exception) { }
        }

        /// <summary>自动化验收用：先记下自己的样子再自动选一个。</summary>
        void ScheduleAutoPick()
        {
            Program.Log("  归类窗自动验收：AutoChoose = " + Program.AutoChoose);
            Dispatcher.UIThread.Post(() => Pick(Program.AutoChoose), DispatcherPriority.Background);
        }
    }
}
