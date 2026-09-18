using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RemeberEr
{
    /// <summary>
    /// 完成记录窗「小小的总结」。照着 src/renderer/record/ 搬，两种模式：
    ///   write —— 还没完成，写一句心得然后 Get Done
    ///   view  —— 已完成，回看心得 + 自动小结
    /// </summary>
    public partial class RecordWindow : Window
    {
        public class Stat
        {
            public string K { get; set; }
            public string V { get; set; }
            public string Small { get; set; }
        }

        readonly Store _store;
        readonly string _id;
        readonly string _mode;

        /// <summary>点了 Get Done：id + 心得文本。</summary>
        public event Action<string, string> Committed;

        public RecordWindow(Store store, string itemId, string mode)
        {
            InitializeComponent();
            _store = store;
            _id = itemId;
            _mode = mode == "write" ? "write" : "view";

            Loaded += (s, e) => Render();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            Interop.HideFromAltTab(new WindowInteropHelper(this).Handle);
            var wa = SystemParameters.WorkArea;
            Left = wa.Left + (wa.Width - Width) / 2;
            Top = wa.Top + (wa.Height - Height) / 2;
        }

        // ============================================================ 取值

        /// <summary>
        /// 注意：elapsedMs / reflectionChars / binMs 这些只在 snapshot 里算好的字段，
        /// 原始条目没有 —— 所以这里按 snapshot() 的同一套公式现算，别直接取。
        /// </summary>
        double ElapsedMs(Dictionary<string, object> it, double now)
        {
            object doneAt = J.Get(it, "doneAt");
            return (doneAt is double ? (double)doneAt : now) - J.Num(it, "createdAt", now);
        }

        double BinMs(Dictionary<string, object> it, double now)
        {
            object binnedAt = J.Get(it, "binnedAt");
            return J.Num(it, "binTotalMs", 0) + (binnedAt is double ? Math.Max(0, now - (double)binnedAt) : 0);
        }

        static string FmtStamp(double ts)
        {
            if (ts <= 0) return "—";
            return DateTimeOffset.FromUnixTimeMilliseconds((long)ts).ToLocalTime()
                .ToString("yyyy-MM-dd HH:mm");
        }

        void SetThumb(Image img, Border ph, TextBlock ch, Dictionary<string, object> it)
        {
            string rel = J.Str(it, "image", null);
            ImageSource src = null;
            if (!string.IsNullOrEmpty(rel))
            {
                string abs = Path.Combine(_store.DataDir, rel.Replace('/', Path.DirectorySeparatorChar));
                src = Images.Get(abs, 200);
            }
            bool has = src != null;
            img.Source = src;
            img.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
            ph.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
            if (!has)
            {
                string t = (J.Str(it, "title", "")).Trim();
                ch.Text = t.Length > 0 ? t.Substring(0, 1) : "·";
            }
        }

        void SetTag(Dictionary<string, object> it)
        {
            bool todo = J.Str(it, "room", "") == "todo";
            TagText.Text = todo ? "Todo" : "Wantdo";
            TagText.Foreground = new SolidColorBrush(todo
                ? Color.FromRgb(0x1d, 0x4e, 0xd8)
                : Color.FromRgb(0x6d, 0x28, 0xd9));
            RoomTag.Background = new SolidColorBrush(todo
                ? Color.FromArgb(0x24, 0x25, 0x63, 0xeb)
                : Color.FromArgb(0x24, 0x8b, 0x5c, 0xf6));
        }

        // ============================================================ 渲染

        void Render()
        {
            var it = _store.Get(_id);
            if (it == null) { Close(); return; }

            double now = Store.Now();
            SetTag(it);
            string ttl = J.Str(it, "title", "").Trim();
            if (ttl.Length == 0) ttl = J.Str(it, "source", "").Trim();
            if (ttl.Length == 0) ttl = "（没有标题）";
            TtlText.Text = ttl;

            if (_mode == "write") RenderWrite(it, now);
            else RenderView(it, now);
        }

        void RenderWrite(Dictionary<string, object> it, double now)
        {
            WritePanel.Visibility = Visibility.Visible;
            ViewPanel.Visibility = Visibility.Collapsed;
            WriteFoot.Visibility = Visibility.Visible;
            ViewFoot.Visibility = Visibility.Collapsed;

            SetThumb(WThumb, WThumbPh, WThumbChar, it);
            WF1.Text = FmtStamp(J.Num(it, "createdAt", 0));
            WF2.Text = Fmt.Dur(ElapsedMs(it, now));
            WF3.Text = (long)J.Num(it, "touchCount", 0) + " 次";

            ReflectionBox.Text = J.Str(it, "reflection", "");
            UpdateCounter();
            ReflectionBox.Focus();
            ReflectionBox.CaretIndex = ReflectionBox.Text.Length;
        }

        void UpdateCounter()
        {
            FootNote.Text = (ReflectionBox.Text ?? "").Trim().Length + " 字";
        }

        void OnReflectionChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            UpdateCounter();
        }

        void RenderView(Dictionary<string, object> it, double now)
        {
            WritePanel.Visibility = Visibility.Collapsed;
            ViewPanel.Visibility = Visibility.Visible;
            WriteFoot.Visibility = Visibility.Collapsed;
            ViewFoot.Visibility = Visibility.Visible;
            FootNote.Text = J.Str(it, "room", "") == "want" ? "这是你曾经想要的东西" : "这件事已经了结";

            SetThumb(VThumb, VThumbPh, VThumbChar, it);
            VF1.Text = J.Str(it, "source", "") is string s && s.Length > 0 ? s : "未知";
            VF2.Text = FmtStamp(J.Num(it, "createdAt", 0));
            VF3.Text = FmtStamp(J.Num(it, "doneAt", 0));

            bool isWant = J.Str(it, "room", "") == "want";
            double elapsed = ElapsedMs(it, now);
            double days = elapsed / 86400000.0;
            string reflection = J.Str(it, "reflection", "");
            int touchCount = (int)J.Num(it, "touchCount", 0);

            // ---- 一句话总结（有加粗片段，所以用 Inlines 拼）----
            SummaryText.Inlines.Clear();
            if (isWant)
            {
                SummaryText.Inlines.Add(new Run("你把它记了 "));
                SummaryText.Inlines.Add(Bold(Fmt.Dur(elapsed)));
                bool any = false;
                if (touchCount > 0)
                {
                    SummaryText.Inlines.Add(new Run("，看了 "));
                    SummaryText.Inlines.Add(Bold(touchCount.ToString()));
                    SummaryText.Inlines.Add(new Run(" 次"));
                    any = true;
                }
                if (reflection.Trim().Length > 0)
                {
                    SummaryText.Inlines.Add(new Run(any ? "，最后写下 " : "，最后写下 "));
                    SummaryText.Inlines.Add(Bold(reflection.Trim().Length.ToString()));
                    SummaryText.Inlines.Add(new Run(" 个字"));
                }
                SummaryText.Inlines.Add(new Run("。"));
            }
            else
            {
                SummaryText.Inlines.Add(new Run("这件事你花了 "));
                SummaryText.Inlines.Add(Bold(Fmt.Dur(elapsed)));
                SummaryText.Inlines.Add(new Run(" 处理完"));

                object doneAt = J.Get(it, "doneAt");
                object dueAt = J.Get(it, "dueAt");
                if (doneAt is double && dueAt is double && (double)dueAt > 0)
                {
                    bool onTime = (double)doneAt <= (double)dueAt;
                    if (onTime)
                    {
                        SummaryText.Inlines.Add(new Run("，"));
                        SummaryText.Inlines.Add(Bold("赶上了期限"));
                        SummaryText.Inlines.Add(new Run("。"));
                    }
                    else
                    {
                        SummaryText.Inlines.Add(new Run("，虽然"));
                        SummaryText.Inlines.Add(Bold("晚了"));
                        SummaryText.Inlines.Add(new Run("，但总算做完了。"));
                    }
                }
                else
                {
                    SummaryText.Inlines.Add(new Run("。"));
                }
            }

            // ---- 心得 ----
            QuoteSection.Visibility = isWant ? Visibility.Visible : Visibility.Collapsed;
            if (isWant)
            {
                bool has = reflection.Trim().Length > 0;
                QuoteText.Text = has ? reflection : "当时没写下什么。也没关系。";
                QuoteText.Foreground = has
                    ? (Brush)FindResource("Tx2")
                    : new SolidColorBrush(Color.FromRgb(0xa1, 0xa1, 0xa6));
                QuoteText.FontStyle = has ? FontStyles.Normal : FontStyles.Italic;
                QuoteBox.BorderBrush = has
                    ? new SolidColorBrush(Color.FromRgb(0xa8, 0x55, 0xf7))
                    : new SolidColorBrush(Color.FromArgb(0x1f, 0, 0, 0));
                QuoteBox.Background = has
                    ? new LinearGradientBrush(
                        Color.FromArgb(0x17, 0xa8, 0x55, 0xf7),
                        Color.FromArgb(0x08, 0xa8, 0x55, 0xf7),
                        new Point(0, 0), new Point(1, 1))
                    : (Brush)new SolidColorBrush(Color.FromArgb(0x05, 0, 0, 0));
            }

            // ---- 小结 ----
            var stats = new List<Stat>
            {
                new Stat { K = "从记下到完成", V = Fmt.Dur(elapsed) },
                new Stat { K = "完成于", V = FmtStamp(J.Num(it, "doneAt", 0)) },
            };

            if (isWant)
            {
                stats.Add(new Stat { K = "你看过它", V = touchCount.ToString(), Small = "次" });
                stats.Add(new Stat { K = "最后一次看它", V = Fmt.RelTime(J.Num(it, "lastTouchedAt", 0)) });
                if (reflection.Trim().Length > 0)
                    stats.Add(new Stat { K = "心得", V = reflection.Trim().Length.ToString(), Small = "字" });
                if (touchCount > 0 && days > 0.5)
                    stats.Add(new Stat { K = "平均多久看一次", V = Fmt.Dur(elapsed / touchCount) });

                int binCount = (int)J.Num(it, "binCount", 0);
                if (binCount > 0)
                    stats.Add(new Stat { K = "在纸篓里待过", V = Fmt.Dur(BinMs(it, now)), Small = binCount + " 次" });
            }

            StatsList.ItemsSource = stats;
        }

        static Run Bold(string s)
        {
            return new Run(s) { FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromRgb(0x1c, 0x1c, 0x1e)) };
        }

        // ============================================================ 交互

        void OnHeaderDrag(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;
            try { DragMove(); } catch (Exception) { }
        }

        void OnClose(object sender, RoutedEventArgs e) { Close(); }
        void OnCancel(object sender, RoutedEventArgs e) { Close(); }

        void OnCommit(object sender, RoutedEventArgs e)
        {
            string r = (ReflectionBox.Text ?? "").Trim();
            var c = Committed;
            if (c != null) c(_id, r);
            Close();
        }

        void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) { Close(); e.Handled = true; return; }
            if (_mode == "write" && e.Key == Key.Enter
                && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                OnCommit(sender, null);
                e.Handled = true;
            }
        }
    }
}
