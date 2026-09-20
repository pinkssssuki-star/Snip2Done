using System;
using System.Collections.Generic;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace RemeberEr
{
    /// <summary>
    /// 详情窗「看它一眼」。
    ///
    /// 行为照原版搬：打字 600ms 防抖自动保存、失焦立即保存、关窗前兜底保存、
    /// Esc / Ctrl(⌘)+Enter 保存并关闭。
    /// Wantdo 点 Get Done 先去写心得，Todo 直接完成并收走。
    /// 窗口大小按图片实际比例算（和 Electron 版 main.js 的 detailSizeFor 同一套算法）。
    /// </summary>
    public partial class DetailWindow : Window
    {
        // 和原版 main.js 里的常量一致
        const int DETAIL_CHROME_H = 288, DETAIL_CHROME_W = 32;
        const int DETAIL_MAX_W = 900, DETAIL_MAX_H = 840;
        // 最小高度从原版的 340 提到 440：
        // 原版的窗口装饰高度常量是按 Windows 标题栏算的，mac 这边没有系统标题栏，
        // 图片区会把备注框挤到几乎贴着底。加大下限让「空图 + 备注」这种最常见的情形
        // 也有舒服的留白（从自检渲染的截图上量出来的，不是拍脑袋）。
        const int DETAIL_MIN_W = 420, DETAIL_MIN_H = 440;

        readonly Store _store;
        readonly string _id;
        Dictionary<string, object> _item;

        DispatcherTimer _saveTimer;
        DispatcherTimer _savedFlash;

        /// <summary>LoadItem 期间字段会被 TextChanged 打到，别让它误触发自动保存与「已保存」闪烁。</summary>
        bool _loading;

        TextBlock _tagText, _srcText, _whenText, _lifeText, _savedText, _doneHint;
        Button _addShotBig, _addShotSmall;
        StackPanel _lifeRow;
        TextBox _titleField, _noteField;
        Image _pic;
        Button _getDoneBtn;

        /// <summary>Wantdo 完成后要写心得，交给上层去开记录窗。</summary>
        public event Action<string> RecordRequested;

        /// <summary>请求给这条补一张截图，交给上层去走框选流程。</summary>
        public event Action<string> AttachShotRequested;

        /// <summary>这扇窗在看哪一条。补截图之后上层要靠它找到该刷新的窗口。</summary>
        public string ItemId { get { return _id; } }

        /// <summary>重读条目内容。补截图之后要调，否则还显示着「补一张截图」的加号。</summary>
        public void Reload()
        {
            // 保存一次再读，避免用户刚打的字被丢掉（重读会把字段覆盖成盘上的值）
            SaveNow();
            LoadItem();
            Program.Log("  详情窗已重读：" + _id);
        }

        public DetailWindow(Store store, string itemId)
        {
            Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);
            _store = store;
            _id = itemId;

            _tagText = this.FindControl<TextBlock>("TagText");
            _srcText = this.FindControl<TextBlock>("SrcText");
            _whenText = this.FindControl<TextBlock>("WhenText");
            _lifeText = this.FindControl<TextBlock>("LifeText");
            _lifeRow = this.FindControl<StackPanel>("LifeRow");
            _savedText = this.FindControl<TextBlock>("SavedText");
            _doneHint = this.FindControl<TextBlock>("DoneHint");
            _addShotBig = this.FindControl<Button>("AddShotBig");
            _addShotSmall = this.FindControl<Button>("AddShotSmall");
            _titleField = this.FindControl<TextBox>("TitleField");
            _noteField = this.FindControl<TextBox>("NoteField");
            _pic = this.FindControl<Image>("Pic");
            _getDoneBtn = this.FindControl<Button>("GetDoneBtn");

            var size = SizeFor(itemId);
            Width = size.Width;
            Height = size.Height;

            Opened += (_, __) =>
            {
                LoadItem();
                CenterOnScreen();
                _titleField.Focus();
            };
        }

        void CenterOnScreen()
        {
            var screen = Screens?.Primary;
            if (screen == null) return;
            double s = screen.Scaling > 0 ? screen.Scaling : 1.0;
            var wa = screen.WorkingArea;
            double x = (wa.X + (wa.Width - Width) / 2) * s;
            double y = (wa.Y + (wa.Height - Height) / 2) * s;
            Position = new Avalonia.PixelPoint((int)Math.Round(x), (int)Math.Round(y));
        }

        /// <summary>按图片实际尺寸算窗口大小 —— 和原版 detailSizeFor 同一套算法。</summary>
        Size SizeFor(string id)
        {
            double iw = 640, ih = 420;
            var it = _store.Get(id);
            string rel = it != null ? J.Str(it, "image", null) : null;
            if (!string.IsNullOrEmpty(rel))
            {
                string abs = Path.Combine(_store.DataDir, rel.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(abs))
                {
                    try
                    {
                        // 只读文件头拿尺寸，不整张解码 —— 原版用 BitmapImage 的 PixelWidth/Height，
                        // 效果一样但代价大得多，这里顺手改好。
                        using (var probe = new Bitmap(abs))
                        {
                            if (probe.PixelSize.Width > 0 && probe.PixelSize.Height > 0)
                            {
                                iw = probe.PixelSize.Width;
                                ih = probe.PixelSize.Height;
                            }
                        }
                    }
                    catch (Exception) { }
                }
            }

            double availW = DETAIL_MAX_W - DETAIL_CHROME_W;
            double availH = DETAIL_MAX_H - DETAIL_CHROME_H;
            double scale = Math.Min(1, Math.Min(availW / iw, availH / ih));

            return new Size(
                Math.Max(DETAIL_MIN_W, Math.Round(iw * scale) + DETAIL_CHROME_W),
                Math.Max(DETAIL_MIN_H, Math.Round(ih * scale) + DETAIL_CHROME_H));
        }

        void LoadItem()
        {
            _loading = true;
            // 先巡检一次：过期的 Wantdo 该进纸篓就进，
            // 别让详情窗显示一个已经该放手的条目还不自知（原版的顺序）
            _store.Sweep();
            _item = _store.Get(_id);
            if (_item == null) { _loading = false; Close(); return; }

            bool isWant = J.Str(_item, "room", "") == "want";
            _tagText.Text = isWant ? "Wantdo" : "Todo";

            string src = J.Str(_item, "source", "");
            _srcText.Text = src.Length > 0 ? src : "未知";
            _whenText.Text = FmtStamp(J.Num(_item, "createdAt", 0));

            // 没有标题就是空的 —— 不写「（没有标题）」那种占位文字（设计原则）
            _titleField.Text = J.Str(_item, "title", "");
            _noteField.Text = J.Str(_item, "note", "");

            if (isWant)
            {
                _lifeRow.IsVisible = true;
                // 注意：life / opacity 这些衰减字段只存在于 snapshot 里，原始条目没有。
                // 直接从原始条目取 life 会永远拿到 0 —— 所以这里按同一套 Decay 现算。
                var cfg = Decay.CfgFor(_store.Settings);
                double tscale = Decay.TimeScaleFor(_store.Settings);
                // v0.9.1：LifeFor 多了一个 demoStartedAt —— 演示加速只应该加速
                // 「演示开始之后」流逝的时间，否则一条一小时前放下的 Wantdo
                // 会在演示开始那一瞬间变成 30000 倍年龄、第一次扫描就被塞进纸篓。
                double life = Decay.LifeFor(_item, Store.Now(), cfg, tscale,
                    Decay.DemoStartedAtFor(_store.Settings));
                int pct = (int)Math.Round(Math.Min(1, Math.Max(0, life)) * 100);
                _lifeText.Text = pct + "%　（被碰过 " + (long)J.Num(_item, "touchCount", 0) + " 次）";
            }
            else
            {
                _lifeRow.IsVisible = false;
            }

            bool done = J.Str(_item, "state", "") == "done";
            _getDoneBtn.IsVisible = !done;
            _doneHint.Text = done ? "这条已经完成了" : (isWant ? "完成时会让你写一句心得" : "");

            string rel = J.Str(_item, "image", null);
            if (!string.IsNullOrEmpty(rel))
            {
                string abs = Path.Combine(_store.DataDir, rel.Replace('/', Path.DirectorySeparatorChar));
                _pic.Source = Images.Get(abs, 900);
            }
            bool hasImg = _pic.Source != null;
            _pic.IsVisible = hasImg;
            // 没图 → 中间一个大按钮；有图 → 右下角小圆钮（不挡内容）
            _addShotBig.IsVisible = !hasImg;
            _addShotSmall.IsVisible = hasImg;

            _loading = false;
        }

        static string FmtStamp(double ts)
        {
            if (ts <= 0) return "—";
            return DateTimeOffset.FromUnixTimeMilliseconds((long)ts).ToLocalTime()
                .ToString("yyyy-MM-dd HH:mm");
        }

        // ============================================================ 保存

        /// <summary>打字时防抖保存，不用手动点保存。</summary>
        void OnFieldChanged(object sender, TextChangedEventArgs e)
        {
            if (_item == null || _loading) return;
            if (_saveTimer == null)
            {
                _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
                _saveTimer.Tick += (s, a) => { _saveTimer.Stop(); SaveNow(); };
            }
            // 重新计时：连续打字只在停下来 600ms 后写一次盘
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        void OnFieldBlur(object sender, RoutedEventArgs e) { SaveNow(); }

        void SaveNow()
        {
            if (_item == null) return;
            if (_saveTimer != null) _saveTimer.Stop();

            var patch = new Dictionary<string, object>
            {
                { "title", _titleField.Text ?? "" },
                { "note", _noteField.Text ?? "" },
            };
            _store.Update(_id, patch);
            _item = _store.Get(_id);
            FlashSaved();
        }

        void FlashSaved()
        {
            _savedText.Opacity = 1;
            if (_savedFlash == null)
            {
                _savedFlash = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
                _savedFlash.Tick += (s, a) => { _savedFlash.Stop(); _savedText.Opacity = 0; };
            }
            _savedFlash.Stop();
            _savedFlash.Start();
        }

        // ============================================================ 交互

        void OnHeaderDrag(object sender, PointerPressedEventArgs e)
        {
            var p = e.GetCurrentPoint(this);
            if (!p.Properties.IsLeftButtonPressed) return;
            if (e.Source is Button) return;
            try { BeginMoveDrag(e); }
            catch (Exception ex) { Program.Log("  详情窗拖动失败：" + ex.Message); }
        }

        /// <summary>点加号：请求上层走补截图流程（复用同一套框选层）。</summary>
        void OnAddShot(object sender, RoutedEventArgs e)
        {
            var h = AttachShotRequested;
            if (h != null) h(_id);
            else Program.Log("  补截图：上层没有接这个请求");
        }

        void OnClose(object sender, RoutedEventArgs e)
        {
            SaveNow();
            Close();
        }

        void OnGetDone(object sender, RoutedEventArgs e)
        {
            SaveNow();
            bool isWant = _item != null && J.Str(_item, "room", "") == "want";

            if (isWant)
            {
                // 先写心得，再算完成 —— 记录窗关掉时才真正 Done
                var r = RecordRequested;
                if (r != null) r(_id);
                return;
            }

            // Todo：直接完成并收走
            _store.Done(_id, false, null, true);
            Close();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                SaveNow();
                Close();
                e.Handled = true;
                return;
            }
            // Ctrl(⌘)+Enter 也当保存并关闭。
            // mac 上把 Ctrl 和 Cmd 都认，用户按哪个都行 ——
            // Windows 版的 Ctrl 在 mac 上习惯落在 Command 上，这里不强制二选一。
            bool ctrl = (e.KeyModifiers & KeyModifiers.Control) != 0;
            bool meta = (e.KeyModifiers & KeyModifiers.Meta) != 0;
            if (e.Key == Key.Enter && (ctrl || meta))
            {
                SaveNow();
                Close();
                e.Handled = true;
            }
            base.OnKeyDown(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            SaveNow();   // 关窗前兜底
            base.OnClosed(e);
        }
    }
}
