using System;
using System.Collections.Generic;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace RemeberEr
{
    /// <summary>
    /// 完成记录窗「小小的总结」，两种模式：
    ///   write — 还没完成，写一句心得然后 Get Done
    ///   view  — 已完成，回看心得 + 自动小结
    ///
    /// 与 WPF 版的对应关系基本是逐行的；差异集中在三处，都写在各自注释里：
    ///   · Run 在 Avalonia 里没有「带字体的构造函数」，要 new 完再设属性；
    ///   · FindResource 不存在，主题色改用一个小的取色辅助；
    ///   · 可见性用 IsVisible 布尔而不是 Visibility 枚举。
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

        Border _roomTag;
        TextBlock _tagText, _ttlText, _footNote;
        StackPanel _writePanel, _viewPanel, _writeFoot, _quoteSection, _noteSection, _wNoteSection;
        Button _viewFoot;
        Image _wThumb, _vThumb;
        Border _wThumbPh, _vThumbPh;
        TextBlock _wThumbChar, _vThumbChar;
        TextBlock _wf1, _wf2, _wf3, _vf1, _vf2, _vf3;
        TextBox _reflectionBox;
        TextBlock _summaryText, _quoteText, _noteText, _wNoteText;
        Border _quoteBox;
        ItemsControl _statsList;

        /// <summary>点了 Get Done：id + 心得文本。</summary>
        public event Action<string, string> Committed;

        /// <summary>这扇窗在看哪一条。用于「同一条再点一次就跳到已开着的那扇」去重。</summary>
        public string ItemId { get { return _id; } }

        public RecordWindow(Store store, string itemId, string mode)
        {
            Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);
            _store = store;
            _id = itemId;
            _mode = mode == "write" ? "write" : "view";

            _roomTag = this.FindControl<Border>("RoomTag");
            _tagText = this.FindControl<TextBlock>("TagText");
            _ttlText = this.FindControl<TextBlock>("TtlText");
            _footNote = this.FindControl<TextBlock>("FootNote");
            _writePanel = this.FindControl<StackPanel>("WritePanel");
            _viewPanel = this.FindControl<StackPanel>("ViewPanel");
            _writeFoot = this.FindControl<StackPanel>("WriteFoot");
            _quoteSection = this.FindControl<StackPanel>("QuoteSection");
            _noteSection = this.FindControl<StackPanel>("NoteSection");
            _noteText = this.FindControl<TextBlock>("NoteText");
            _wNoteSection = this.FindControl<StackPanel>("WNoteSection");
            _wNoteText = this.FindControl<TextBlock>("WNoteText");
            _viewFoot = this.FindControl<Button>("ViewFoot");
            _wThumb = this.FindControl<Image>("WThumb");
            _vThumb = this.FindControl<Image>("VThumb");
            _wThumbPh = this.FindControl<Border>("WThumbPh");
            _vThumbPh = this.FindControl<Border>("VThumbPh");
            _wThumbChar = this.FindControl<TextBlock>("WThumbChar");
            _vThumbChar = this.FindControl<TextBlock>("VThumbChar");
            _wf1 = this.FindControl<TextBlock>("WF1");
            _wf2 = this.FindControl<TextBlock>("WF2");
            _wf3 = this.FindControl<TextBlock>("WF3");
            _vf1 = this.FindControl<TextBlock>("VF1");
            _vf2 = this.FindControl<TextBlock>("VF2");
            _vf3 = this.FindControl<TextBlock>("VF3");
            _reflectionBox = this.FindControl<TextBox>("ReflectionBox");
            _summaryText = this.FindControl<TextBlock>("SummaryText");
            _quoteText = this.FindControl<TextBlock>("QuoteText");
            _quoteBox = this.FindControl<Border>("QuoteBox");
            _statsList = this.FindControl<ItemsControl>("StatsList");

            _reflectionBox.TextChanged += (_, __) => UpdateCounter();

            Opened += (_, __) =>
            {
                Render();
                CenterOnScreen();
            };
        }

        void CenterOnScreen()
        {
            var screen = Screens?.Primary;
            if (screen == null) return;
            double s = screen.Scaling > 0 ? screen.Scaling : 1.0;
            var wa = screen.WorkingArea;
            Position = new Avalonia.PixelPoint(
                (int)Math.Round((wa.X + (wa.Width - Width) / 2) * s),
                (int)Math.Round((wa.Y + (wa.Height - Height) / 2) * s));
        }

        /// <summary>
        /// 从主题资源里取一个画刷。
        /// Avalonia 没有 WPF 的 FindResource，顺带把「取不到就回落」也写清楚，
        /// 免得以后改主题时拿到 null 直接空引用。
        /// </summary>
        IBrush Res(string key, string fallback)
        {
            if (this.TryFindResource(key, out var v) && v is IBrush b) return b;
            return new SolidColorBrush(Color.Parse(fallback));
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
            Bitmap src = null;
            if (!string.IsNullOrEmpty(rel))
            {
                string abs = Path.Combine(_store.DataDir, rel.Replace('/', Path.DirectorySeparatorChar));
                src = Images.Get(abs, 200);
            }
            bool has = src != null;
            img.Source = src;
            img.IsVisible = has;
            ph.IsVisible = !has;
            if (!has)
            {
                string t = (J.Str(it, "title", "")).Trim();
                ch.Text = t.Length > 0 ? StringInfoSafe(t) : "·";
            }
        }

        /// <summary>取首字。按字素取，避免把 emoji 劈成半个（和 FirstChar 转换器同一套做法）。</summary>
        static string StringInfoSafe(string s)
        {
            var e = System.Globalization.StringInfo.GetTextElementEnumerator(s);
            return e.MoveNext() ? e.GetTextElement() : "·";
        }

        void SetTag(Dictionary<string, object> it)
        {
            bool todo = J.Str(it, "room", "") == "todo";
            _tagText.Text = todo ? "Todo" : "Wantdo";
            _tagText.Foreground = new SolidColorBrush(todo
                ? Color.FromRgb(0x1d, 0x4e, 0xd8)
                : Color.FromRgb(0x6d, 0x28, 0xd9));
            _roomTag.Background = new SolidColorBrush(todo
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
            _ttlText.Text = ttl;

            if (_mode == "write") RenderWrite(it, now);
            else RenderView(it, now);
        }

        void RenderWrite(Dictionary<string, object> it, double now)
        {
            _writePanel.IsVisible = true;
            FillNote(it);
            _viewPanel.IsVisible = false;
            _writeFoot.IsVisible = true;
            _viewFoot.IsVisible = false;

            SetThumb(_wThumb, _wThumbPh, _wThumbChar, it);
            _wf1.Text = FmtStamp(J.Num(it, "createdAt", 0));
            _wf2.Text = Fmt.Dur(ElapsedMs(it, now));
            _wf3.Text = (long)J.Num(it, "touchCount", 0) + " 次";

            _reflectionBox.Text = J.Str(it, "reflection", "");
            UpdateCounter();
            _reflectionBox.Focus();
            _reflectionBox.CaretIndex = (_reflectionBox.Text ?? "").Length;
        }

        void UpdateCounter()
        {
            _footNote.Text = (_reflectionBox.Text ?? "").Trim().Length + " 字";
        }

        /// <summary>
        /// 把条目上写的备注填进当前这一屏（写心得 / 回看都有）。
        ///
        /// 和「心得」不是一回事：备注是记下这条时写的，心得是完成时写的。
        /// 两种模式共用这一个填充点，免得两边各写一份、日后走散。
        /// </summary>
        void FillNote(Dictionary<string, object> it)
        {
            string note = J.Str(it, "note", "");
            bool has = !string.IsNullOrWhiteSpace(note);

            _noteSection.IsVisible = has && _viewPanel.IsVisible;
            _noteText.Text = note;

            _wNoteSection.IsVisible = has && _writePanel.IsVisible;
            _wNoteText.Text = note;
        }

        void RenderView(Dictionary<string, object> it, double now)
        {
            _writePanel.IsVisible = false;
            _viewPanel.IsVisible = true;
            _writeFoot.IsVisible = false;
            _viewFoot.IsVisible = true;
            _footNote.Text = J.Str(it, "room", "") == "want" ? "这是你曾经想要的东西" : "这件事已经了结";

            SetThumb(_vThumb, _vThumbPh, _vThumbChar, it);
            string src = J.Str(it, "source", "");
            _vf1.Text = src.Length > 0 ? src : "未知";
            _vf2.Text = FmtStamp(J.Num(it, "createdAt", 0));
            _vf3.Text = FmtStamp(J.Num(it, "doneAt", 0));

            bool isWant = J.Str(it, "room", "") == "want";
            double elapsed = ElapsedMs(it, now);
            double days = elapsed / 86400000.0;
            string reflection = J.Str(it, "reflection", "");
            FillNote(it);
            int touchCount = (int)J.Num(it, "touchCount", 0);

            // ---- 一句话总结（有加粗片段，所以用 Inlines 拼）----
            _summaryText.Inlines.Clear();
            if (isWant)
            {
                AddRun(_summaryText, "你把它记了 ");
                AddBold(_summaryText, Fmt.Dur(elapsed));
                bool any = false;
                if (touchCount > 0)
                {
                    AddRun(_summaryText, "，看了 ");
                    AddBold(_summaryText, touchCount.ToString());
                    AddRun(_summaryText, " 次");
                    any = true;
                }
                if (reflection.Trim().Length > 0)
                {
                    AddRun(_summaryText, any ? "，最后写下 " : "，最后写下 ");
                    AddBold(_summaryText, reflection.Trim().Length.ToString());
                    AddRun(_summaryText, " 个字");
                }
                AddRun(_summaryText, "。");
            }
            else
            {
                AddRun(_summaryText, "这件事你花了 ");
                AddBold(_summaryText, Fmt.Dur(elapsed));
                AddRun(_summaryText, " 处理完");

                object doneAt = J.Get(it, "doneAt");
                object dueAt = J.Get(it, "dueAt");
                if (doneAt is double && dueAt is double && (double)dueAt > 0)
                {
                    bool onTime = (double)doneAt <= (double)dueAt;
                    if (onTime)
                    {
                        AddRun(_summaryText, "，");
                        AddBold(_summaryText, "赶上了期限");
                        AddRun(_summaryText, "。");
                    }
                    else
                    {
                        AddRun(_summaryText, "，虽然");
                        AddBold(_summaryText, "晚了");
                        AddRun(_summaryText, "，但总算做完了。");
                    }
                }
                else
                {
                    AddRun(_summaryText, "。");
                }
            }

            // ---- 心得 ----
            _quoteSection.IsVisible = isWant;
            if (isWant)
            {
                bool has = reflection.Trim().Length > 0;
                _quoteText.Text = has ? reflection : "当时没写下什么。也没关系。";
                _quoteText.Foreground = has
                    ? Res("Tx2", "#4a4a55")
                    : new SolidColorBrush(Color.FromRgb(0xa1, 0xa1, 0xa6));
                _quoteText.FontStyle = has ? FontStyle.Normal : FontStyle.Italic;
                _quoteBox.BorderBrush = has
                    ? new SolidColorBrush(Color.FromRgb(0xa8, 0x55, 0xf7))
                    : new SolidColorBrush(Color.FromArgb(0x1f, 0, 0, 0));
                _quoteBox.Background = has
                    ? new LinearGradientBrush
                    {
                        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                        EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                        GradientStops =
                        {
                            new GradientStop(Color.FromArgb(0x17, 0xa8, 0x55, 0xf7), 0),
                            new GradientStop(Color.FromArgb(0x08, 0xa8, 0x55, 0xf7), 1),
                        },
                    }
                    : (IBrush)new SolidColorBrush(Color.FromArgb(0x05, 0, 0, 0));
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

            _statsList.ItemsSource = stats;
        }

        /// <summary>
        /// 追加一段普通文字。
        /// Avalonia 的 Inline 没有 WPF 那种「带字体的构造函数」，
        /// 只能 new 完再设属性（这是两版 XAML 方言差异里比较实际的一条）。
        /// </summary>
        static void AddRun(TextBlock tb, string text)
        {
            tb.Inlines?.Add(new Run { Text = text });
        }

        /// <summary>追加一段加粗文字。原版用 Run{ FontWeight, Foreground }，这里等价。</summary>
        static void AddBold(TextBlock tb, string text)
        {
            tb.Inlines?.Add(new Run
            {
                Text = text,
                FontWeight = FontWeight.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x1c, 0x1c, 0x1e)),
            });
        }

        // ============================================================ 交互

        void OnHeaderDrag(object sender, PointerPressedEventArgs e)
        {
            var p = e.GetCurrentPoint(this);
            if (!p.Properties.IsLeftButtonPressed) return;
            if (e.Source is Button) return;
            try { BeginMoveDrag(e); }
            catch (Exception ex) { Program.Log("  记录窗拖动失败：" + ex.Message); }
        }

        void OnClose(object sender, RoutedEventArgs e) { Close(); }
        void OnCancel(object sender, RoutedEventArgs e) { Close(); }

        void OnCommit(object sender, RoutedEventArgs e)
        {
            string r = (_reflectionBox.Text ?? "").Trim();
            var c = Committed;
            if (c != null) c(_id, r);
            Close();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Escape) { Close(); e.Handled = true; return; }

            // 写心得时 Ctrl(⌘)+Enter 直接提交 —— mac 上两个修饰键都认
            bool ctrl = (e.KeyModifiers & KeyModifiers.Control) != 0;
            bool meta = (e.KeyModifiers & KeyModifiers.Meta) != 0;
            if (_mode == "write" && e.Key == Key.Enter && (ctrl || meta))
            {
                OnCommit(null, null);   // OnCommit 不使用 sender
                e.Handled = true;
            }
            base.OnKeyDown(e);
        }
    }
}
