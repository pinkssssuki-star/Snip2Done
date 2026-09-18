using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace RemeberEr
{
    /// <summary>
    /// 详情窗「看它一眼」。照着 src/renderer/detail/ 搬：
    /// 打字 600ms 防抖自动保存、失焦立即保存、关窗前兜底保存、Esc / Ctrl+Enter 保存并关闭。
    /// Wantdo 点 Get Done 先去写心得，Todo 直接完成并收走。
    /// </summary>
    public partial class DetailWindow : Window
    {
        // 和 main.js 里的常量一致
        const int DETAIL_CHROME_H = 288, DETAIL_CHROME_W = 32;
        const int DETAIL_MAX_W = 900, DETAIL_MAX_H = 840;
        const int DETAIL_MIN_W = 420, DETAIL_MIN_H = 340;

        readonly Store _store;
        readonly string _id;
        Dictionary<string, object> _item;

        DispatcherTimer _saveTimer;
        DispatcherTimer _savedFlash;
        /// <summary>LoadItem 期间会被字段的 TextChanged 打到，别让它误触发自动保存和「已保存」闪烁。</summary>
        bool _loading;

        /// <summary>Wantdo 完成后要写心得，交给主窗口去开记录窗。</summary>
        public event Action<string> RecordRequested;

        public DetailWindow(Store store, string itemId)
        {
            InitializeComponent();
            _store = store;
            _id = itemId;

            var size = SizeFor(itemId);
            Width = size.Width;
            Height = size.Height;

            Loaded += (s, e) => { LoadItem(); TitleField.Focus(); };
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            Interop.HideFromAltTab(new WindowInteropHelper(this).Handle);
            // 居中在工作区
            var wa = SystemParameters.WorkArea;
            Left = wa.Left + (wa.Width - Width) / 2;
            Top = wa.Top + (wa.Height - Height) / 2;
        }

        /// <summary>按图片实际尺寸算窗口大小 —— 和 main.js 的 detailSizeFor 同一套算法。</summary>
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
                        var b = new BitmapImage();
                        b.BeginInit();
                        b.CacheOption = BitmapCacheOption.OnLoad;
                        b.UriSource = new Uri(abs, UriKind.Absolute);
                        b.EndInit();
                        if (b.PixelWidth > 0 && b.PixelHeight > 0) { iw = b.PixelWidth; ih = b.PixelHeight; }
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
            // 先巡检一次：过期的 Wantdo 该进纸篓就进，别让详情窗显示一个已经该放手的条目还不自知
            _store.Sweep();
            _item = _store.Get(_id);
            if (_item == null) { _loading = false; Close(); return; }

            bool isWant = J.Str(_item, "room", "") == "want";
            TagText.Text = isWant ? "Wantdo" : "Todo";
            string src = J.Str(_item, "source", "");
            SrcText.Text = src.Length > 0 ? src : "未知";
            WhenText.Text = FmtStamp(J.Num(_item, "createdAt", 0));

            // 没有标题就是空的 —— 不写「（没有标题）」那种占位文字
            TitleField.Text = J.Str(_item, "title", "");
            NoteField.Text = J.Str(_item, "note", "");

            if (isWant)
            {
                LifeRow.Visibility = Visibility.Visible;
                // 注意：life / opacity 这些衰减字段只存在于 snapshot 里，原始条目没有。
                // 直接从原始条目取 life 会永远拿到 0 —— 所以这里按同一套 Decay 现算。
                var cfg = Decay.CfgFor(_store.Settings);
                double scale = Decay.TimeScaleFor(_store.Settings);
                double life = Decay.LifeFor(_item, Store.Now(), cfg, scale, Decay.DemoStartedAtFor(_store.Settings));
                int pct = (int)Math.Round(Math.Min(1, Math.Max(0, life)) * 100);
                LifeText.Text = pct + "%　（被碰过 " + (long)J.Num(_item, "touchCount", 0) + " 次）";
            }
            else
            {
                LifeRow.Visibility = Visibility.Collapsed;
            }

            bool done = J.Str(_item, "state", "") == "done";
            GetDoneBtn.Visibility = done ? Visibility.Collapsed : Visibility.Visible;
            DoneHint.Text = done ? "这条已经完成了" : (isWant ? "完成时会让你写一句心得" : "");

            string rel = J.Str(_item, "image", null);
            if (!string.IsNullOrEmpty(rel))
            {
                string abs = Path.Combine(_store.DataDir, rel.Replace('/', Path.DirectorySeparatorChar));
                Pic.Source = Images.Get(abs, 900);
            }
            bool hasImg = Pic.Source != null;
            NoImage.Visibility = hasImg ? Visibility.Collapsed : Visibility.Visible;
            Pic.Visibility = hasImg ? Visibility.Visible : Visibility.Collapsed;

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
        void OnFieldChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (_item == null || _loading) return;
            if (_saveTimer == null)
            {
                _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
                _saveTimer.Tick += (s, a) => { _saveTimer.Stop(); SaveNow(); };
            }
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
                { "title", TitleField.Text ?? "" },
                { "note", NoteField.Text ?? "" },
            };
            _store.Update(_id, patch);
            _item = _store.Get(_id);
            FlashSaved();
        }

        void FlashSaved()
        {
            SavedText.Opacity = 1;
            if (_savedFlash == null)
            {
                _savedFlash = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
                _savedFlash.Tick += (s, a) => { _savedFlash.Stop(); SavedText.Opacity = 0; };
            }
            _savedFlash.Stop();
            _savedFlash.Start();
        }

        // ============================================================ 交互

        void OnHeaderDrag(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;
            try { DragMove(); } catch (Exception) { }
        }

        void OnClose(object sender, RoutedEventArgs e) { SaveNow(); Close(); }

        void OnGetDone(object sender, RoutedEventArgs e)
        {
            SaveNow();
            bool isWant = _item != null && J.Str(_item, "room", "") == "want";

            if (isWant)
            {
                // 先写心得，再算完成
                var r = RecordRequested;
                if (r != null) r(_id);
                return;
            }

            // Todo：直接完成并收走
            _store.Done(_id, false, null, true);
            Close();
        }

        void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                SaveNow();
                Close();
                e.Handled = true;
                return;
            }
            // Ctrl+Enter 也当保存并关闭
            if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                SaveNow();
                Close();
                e.Handled = true;
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            SaveNow();   // 关窗前兜底
            base.OnClosed(e);
        }
    }
}
