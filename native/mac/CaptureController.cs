using System;
using System.Collections.Generic;
using System.IO;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace RemeberEr
{
    /// <summary>
    /// 采集流程的编排：热键 → 抓屏 → 框选 → 归类 → 落盘 → 刷新面板。
    ///
    /// 为什么单独拎一个类而不是塞进 PanelWindow：
    ///   · 这条链路横跨平台层（抓屏/热键/窗口探测）和三个窗口（框选层/归类窗/面板），
    ///     放在面板里会让 PanelWindow 同时管 UI 和业务流程；
    ///   · 更重要的是它**可以被独立驱动**：RunFromScreenshot 接收一张现成的位图，
    ///     因此「框选 → 归类 → 落盘 → 刷新」这一段不需要屏幕录制权限就能验证。
    ///     抓屏是整条链路里唯一需要权限的一步，把它隔离在 Trigger 里。
    ///
    /// 顺序上有一处关键约束（原版注释里也记着）：
    /// 取「鼠标下窗口标题」必须在**自己的浮层出现之前**完成，
    /// 否则探到的只会是浮层自己。
    /// </summary>
    public class CaptureController
    {
        readonly Store _store;
        readonly PanelWindow _panel;

        OverlayWindow _overlay;
        ChooserWindow _chooser;
        bool _busy;

        /// <summary>
        /// 补截图的目标条目。非 null 时，框选完之后**跳过归类窗**直接挂图 ——
        /// 归类早在这条被记下来的时候就定过了，再问一次是多余的。
        /// </summary>
        string _attachToId;

        /// <summary>当前钉在屏幕上的贴图。持着引用只是为了别让它们被 GC ——
        /// 关掉之后元素自己会关窗，这里的列表只是记账。</summary>
        readonly List<PinWindow> _pins = new List<PinWindow>();



        public CaptureController(Store store, PanelWindow panel)
        {
            _store = store;
            _panel = panel;
        }

        public bool Busy { get { return _busy; } }

        /// <summary>注册全局热键。原版是 Ctrl+Shift+*，mac 上换成 ⌘⇧*。</summary>
        public void RegisterHotkeys()
        {
            // Carbon 虚拟键码：A=0, H=4, Q=12（不是字符码，是物理键位）
            Program.Platform.RegisterHotkey("capture", HotkeyModifiers.Win | HotkeyModifiers.Shift, 0,
                () => Trigger("hotkey"));
            Program.Platform.RegisterHotkey("toggle", HotkeyModifiers.Win | HotkeyModifiers.Shift, 4,
                TogglePanel);
            Program.Platform.RegisterHotkey("quit", HotkeyModifiers.Win | HotkeyModifiers.Shift, 12,
                () => { Program.Log("  [热键] 退出"); Program.Platform.UnregisterAllHotkeys(); _panel.Close(); });
        }

        /// <summary>
        /// 热键自检：核对注册数量，并合成一次派发，确认「热键 → 回调」这条线是通的。
        /// </summary>
        public void HotkeySelfTest()
        {
            Program.Log("=== 全局热键自检 ===");
            Program.Log("  已注册热键数 = " + MacHotkeys.RegisteredCount + "（期望 3：capture / toggle / quit）");

            foreach (var id in new[] { "capture", "toggle", "quit" })
            {
                string detail;
                bool ok = MacHotkeys.SynthesizeHotkeyPress(id, out detail);
                Program.Log(string.Format("  {0,-8} → {1}　{2}",
                    id, ok ? "派发成功" : "派发失败", detail));
            }
            Program.Log("=== 热键自检结束 ===");
        }

        void TogglePanel()
        {
            Program.Log("  [热键] 显示/隐藏面板");
            if (_panel.IsVisible) _panel.Hide();
            else _panel.Show();
        }

        // ============================================================ 抓屏（需要权限）

        /// <summary>从真实屏幕开始一次采集。这是唯一需要「屏幕录制」权限的一步。</summary>
        public void Trigger(string why)
        {
            if (_busy) { Program.Log("  [采集] 已有一次采集在进行，忽略 " + why); return; }

            Program.Log("  [采集] 触发（" + why + "）");

            if (!Program.Platform.HasScreenCapturePermission())
            {
                Program.Log("  [采集] 没有「屏幕录制」权限，无法抓屏。");
                Program.Log("         请到 系统设置 → 隐私与安全性 → 屏幕录制 勾选本程序（终端），然后重启。");
                return;
            }

            LiveCapture(why);
        }

        /// <summary>
        /// 从**真实屏幕**走一次完整采集：探测窗口 → 抓屏 → 框选 → 归类 → 落盘。
        ///
        /// 与 RunFromScreenshot 的分工：那个是「喂一张现成位图」的自动化入口
        /// （用于免权限验收），这个是真的去抓屏。两者从这里开始合流。
        /// </summary>
        public void LiveCapture(string why)
        {
            // ⚠️ 探测「鼠标下的窗口」必须在自己的浮层出现**之前** ——
            // 否则框选层一盖上去，探到的就只会是我们自己。
            var disp = PickDisplayUnderCursor();
            var fg = Program.Platform.ProbeForegroundWindow();
            Program.Log(string.Format("  [采集] 目标屏 = {0}；来源 = {1} / 标题 = {2}",
                disp, fg.Source, fg.Title));

            // 面板**不用藏** —— 抓屏时由 ScreenCaptureKit 的过滤器把本进程的窗口
            // 排除掉（见 sck_bridge.m 里的 excludingWindows）。
            //
            // 为什么不用「藏起来再恢复」：那条路要改两次窗口状态，
            // 而且实测在测试环境里会让 SCK 的回调超时（15s 不返回）。
            // 从过滤器里排除不动任何窗口状态，SCK 本身就是为此设计的，更干净。
            GrabAndOverlay(disp, fg, why);
        }

        /// <summary>真正抓屏并铺开框选层。面板已藏好之后才调。</summary>
        void GrabAndOverlay(DisplayInfo disp, ForegroundInfo fg, string why)
        {
            var shot = Program.Platform.CaptureDisplay(disp);
            if (shot == null)
            {
                Program.Log("  [采集] 抓屏失败（见上面原因）");
                RestorePanel();
                _busy = false;
                return;
            }

            RunFromScreenshot(shot, disp, fg, why);
        }

        /// <summary>
        /// 采集结束时的收尾。
        ///
        /// 面板不藏也不移（靠过滤器排除），所以这里其实没有要恢复的东西 ——
        /// 保留这个调用点是为了让「采集无论成败都要走收尾」这件事在代码里显式可见，
        /// 以后真要在采集期间改窗口状态，接在这里就行。
        /// </summary>
        void RestorePanel()
        {
            _panel.Vm.Refresh();
        }

        /// <summary>光标所在的那块屏；取不到就用主屏。</summary>
        DisplayInfo PickDisplayUnderCursor()
        {
            var ds = Program.Platform.GetDisplays();
            if (ds.Count == 0) return null;
            if (Program.PrimaryOnly) foreach (var d in ds) if (d.IsPrimary) return d;

            var c = Program.Platform.GetCursorPosition();
            foreach (var d in ds)
            {
                if (c.X >= d.X && c.X < d.X + d.W && c.Y >= d.Y && c.Y < d.Y + d.H) return d;
            }
            foreach (var d in ds) if (d.IsPrimary) return d;
            return ds[0];
        }

        /// <summary>
        /// 给一条已有条目补一张截图。
        ///
        /// 走的是**和截图主流程同一套框选层** —— 不另写一份抓屏/选区代码，
        /// 所以多屏、缩放、快速模式这些行为天然一致（上游也是这么做的）。
        /// 区别只有一处：框选完之后不弹归类窗，直接把图挂到这条上。
        /// </summary>
        public void StartCaptureForItem(string itemId)
        {
            if (string.IsNullOrEmpty(itemId)) return;
            var it = _store.Get(itemId);
            if (it == null) { Program.Log("  !! 补截图：目标条目已不存在"); return; }
            if (_busy) { Program.Log("  [采集] 已有一次采集在进行，忽略补截图"); return; }
            if (!Program.Platform.HasScreenCapturePermission())
            {
                Program.Log("  [补图] 没有「屏幕录制」权限，无法抓屏。");
                return;
            }

            _attachToId = itemId;
            Program.Log(string.Format("  补截图：目标 {0}「{1}」",
                itemId.Length > 8 ? itemId.Substring(0, 8) : itemId, J.Str(it, "title", "")));

            LiveCapture("补图");
        }

        /// <summary>把截好的图挂到目标条目上，并让已经开着的详情窗重读。</summary>
        void AttachImage(string id, byte[] png)
        {
            if (png == null) { Program.Log("  !! 补图失败：没有图片数据"); return; }
            string rel = _store.AttachImage(id, png);
            if (rel == null) { Program.Log("  !! 补图失败：条目不存在"); return; }

            Program.Log(string.Format("  补图成功 -> {0}（{1}KB）", rel, png.Length / 1024));

            // 已经开着的那扇详情窗要重读 —— 不然它还杵着那个「补一张截图」的加号。
            // （上游为此专门加了一次 Reload，理由写得很直白：加完图窗口不刷新很奇怪。）
            _panel.ReloadDetailFor(id);
            _panel.Vm.Refresh();
        }

        // ============================================================ 框选 → 归类 → 落盘

        /// <summary>
        /// 用一张现成的位图走完「框选 → 归类 → 落盘 → 刷新面板」。
        ///
        /// 分开成公开方法是为了让这一段能**脱离权限**被验证 ——
        /// 自动化验收（--flow=chain）喂一张合成位图进来即可覆盖全过程。
        /// </summary>
        public void RunFromScreenshot(Screenshot shot, DisplayInfo disp, ForegroundInfo fg, string why)
        {
            _busy = true;

            // 压暗要作用在**传进来的这张**上，不能再调一次抓屏。
            // 之前这里写的是 CaptureDisplayDimmed(...)，它在 mac 实现里会重新抓一次屏 ——
            // 于是「喂一张现成位图」的自动化路径照样会因为没权限而失败，
            // 合成位图被整个丢掉。这是链路自检抓出来的真实缺陷。
            var dimmed = shot;

            _overlay = new OverlayWindow(dimmed, disp, Program.ForceQuick || _panel.Vm.QuickCapture);
            _overlay.Picked += (ix, iy, iw, ih) =>
            {
                // 回调里任何异常都可能被 UI 框架吞掉，从而表现为「点了没反应」。
                // 采集链路是核心流程，这里必须自己接住并留痕。
                try
                {
                    Program.Log(string.Format("  [采集] 选定区域（物理像素，相对本屏）= ({0},{1}) {2}x{3}", ix, iy, iw, ih));
                    CloseOverlay();

                    // 补截图：跳过归类窗，直接挂图
                    if (_attachToId != null)
                    {
                        string to = _attachToId;
                        _attachToId = null;
                        var cropAttach = CropBgra(shot, ix, iy, iw, ih);
                        AttachImage(to, EncodePng(cropAttach));
                        RestorePanel();
                        _busy = false;
                        return;
                    }

                    ShowChooser(shot, disp, fg, ix, iy, iw, ih);
                }
                catch (Exception ex)
                {
                    Program.Log("  !! [采集] 选区回调异常：" + ex.GetType().Name + " " + ex.Message);
                    Program.Log("     " + ex.StackTrace);
                    _busy = false;
                }
            };
            _overlay.Pinned += (ix, iy, iw, ih) =>
            {
                // 贴图：钉在屏幕上，**不落库** —— 用户只是想「把这张图摆在那儿」，
                // 不是记一件事。所以这里不碰 Store，也不弹归类窗。
                try
                {
                    Program.Log(string.Format("  [采集] 贴图（物理像素，相对本屏）= ({0},{1}) {2}x{3}", ix, iy, iw, ih));
                    var cropPin = CropBgra(shot, ix, iy, iw, ih);
                    var bmp = BitmapUtil.ToBitmap(cropPin);
                    if (bmp == null) { Program.Log("  !! 贴图失败：图片数据为空"); }
                    else
                    {
                        // 贴图摆在选区原来的位置上（用户框在哪就钉在哪），
                        // 所以要把「相对本屏的物理坐标」换算成**全局逻辑坐标**。
                        double sc = disp.Scale > 0 ? disp.Scale : 1.0;
                        double logicalX = (disp.X + ix) / sc;
                        double logicalY = (disp.Y + iy) / sc;

                        _pins.Add(new PinWindow(bmp, iw, ih, sc, logicalX, logicalY));
                        _pins[_pins.Count - 1].Show();
                        Program.Log("  贴图数 = " + _pins.Count);
                    }
                }
                catch (Exception ex)
                {
                    Program.Log("  !! 贴图失败：" + ex.GetType().Name + " " + ex.Message);
                }
                CloseOverlay();
                RestorePanel();
                _busy = false;
            };

            _overlay.Cancelled += () =>
            {
                Program.Log("  [采集] 已取消" + (_attachToId != null ? "（补图）" : ""));
                _attachToId = null;
                CloseOverlay();
                RestorePanel();
                _busy = false;
            };

            _overlay.Show();
            // 自动验收：不等人拖拽，直接给一个居中的选区
            if (Program.AutoRegionW > 0)
            {
                int w = Program.AutoRegionW, h = Program.AutoRegionH;
                int x = Math.Max(0, ((int)shot.Width - w) / 2);
                int y = Math.Max(0, ((int)shot.Height - h) / 2);
                Program.Log(string.Format("  [采集] 自动验收选区 = ({0},{1}) {2}x{3}", x, y, w, h));
                // confirm: true —— 走完 Picked 这条真实出口，链路才会继续到归类窗
                _overlay.SetSelectionForTest(x, y, w, h, Program.OverlayShot, true);
            }
        }

        void CloseOverlay()
        {
            if (_overlay == null) return;
            try { _overlay.Dismiss(); } catch (Exception) { }
            _overlay = null;
        }

        void ShowChooser(Screenshot shot, DisplayInfo disp, ForegroundInfo fg,
                         int ix, int iy, int iw, int ih)
        {
            // 裁选区 → PNG（落库用）+ 预览位图
            var crop = CropBgra(shot, ix, iy, iw, ih);
            byte[] png = null;
            Bitmap preview = null;
            try { png = EncodePng(crop); } catch (Exception ex) { Program.Log("  裁图/编码失败：" + ex.Message); }
            preview = BitmapUtil.ToBitmap(crop);

            _chooser = new ChooserWindow(new ChooserWindow.Payload
            {
                Png = png,
                Preview = preview,
                App = fg != null ? fg.Source : "",
                Title = fg != null ? fg.Title : "",
            }, disp, ix, iy, iw, ih);

            _chooser.Decided += (choice, typed, days) =>
            {
                Program.Log(string.Format("  [采集] 归类结果 = {0}，标题 = {1}，天数 = {2}",
                    choice ?? "(取消)", typed, days > 0 ? days + " 天" : "（随机 48~72 小时）"));
                if (choice != null) Save(choice, typed, days, png, fg);
                _chooser = null;
                _busy = false;
            };

            // ⚠️ 顺序要紧：自动验收必须先接好 Decided 再触发选一个。
            // 反过来写会先让归类窗自己关掉，然后 Show() 就抛
            // 「Cannot re-show a closed window」（踩过）。
            if (Program.AutoDays > 0 && !string.IsNullOrEmpty(Program.AutoChoose))
            {
                _chooser.AutoPickWithDays(Program.AutoChoose, Program.AutoDays);
                return;   // 已经选完并关了，不要再 Show
            }

            _chooser.Show();
        }

        /// <summary>按归类结果落盘：room / dueBucket / 标题 / 来源 / 图片 / 自定义天数。</summary>
        void Save(string choice, string typed, int days, byte[] png, ForegroundInfo fg)
        {
            string room, bucket;
            if (choice == "want") { room = "want"; bucket = null; }
            else if (choice == "today") { room = "todo"; bucket = "today"; }
            else { room = "todo"; bucket = "soon"; }

            // 标题留空则回落到截图时的窗口标题（原版规则）
            string title = typed;
            if (string.IsNullOrWhiteSpace(title) && fg != null) title = fg.Title;
            string source = fg != null ? fg.Source : "";

            // dueDays 只有「两三天」那一档有意义：填了就是 N 天后，没填交给数据层随机 48~72 小时。
            // Today 档必须传 0 —— 否则那个没意义的数字会被写进数据里
            // （实测过：Today 也存了 dueDays=9，dueAt 虽然是对的，但这条字段是脏的）。
            int dueDays = bucket == "soon" ? days : 0;
            var item = _store.Add(room, title ?? "", source, null, bucket, null, dueDays);
            string id = J.Str(item, "id", null);

            if (png != null && id != null)
            {
                // 直接改条目对象上的 image 字段，**不要**走 Update(id, patch)。
                //
                // 原因：Store.Update 只认 title / note / reflection 三个键，
                // 传 image 进去会被静默忽略（踩过：PNG 明明写出来了，
                // data.json 里 image 还是 null，条目显示成「没有截图」）。
                // 原版 Windows 也是这么写的（PanelWindow.xaml.cs: item["image"] = rel），
                // 这里保持一致，顺带把 Store.Update 的这个局限记在这一处。
                string rel = _store.SaveImage(id, png);
                item["image"] = rel;

                // Add 已经存过一次，这里把带 image 的状态落盘
                _store.Update(id, new Dictionary<string, object> { { "title", title ?? "" } });
            }

            Program.Log(string.Format("  [采集] 已落盘：id={0} room={1} bucket={2} dueDays={3} 图片={4}",
                id, room, bucket ?? "—", dueDays > 0 ? dueDays.ToString() : "—",
                png != null ? (png.Length / 1024) + "KB" : "无"));

            // 收尾（面板刷新等；面板本身没被藏过，见 RestorePanel 的说明）
            RestorePanel();
        }

        // ============================================================ 位图工具

        /// <summary>从整屏位图里裁一块（物理像素，相对该屏左上角）。</summary>
        static Screenshot CropBgra(Screenshot src, int x, int y, int w, int h)
        {
            x = Math.Max(0, Math.Min(x, src.Width - 1));
            y = Math.Max(0, Math.Min(y, src.Height - 1));
            w = Math.Max(1, Math.Min(w, src.Width - x));
            h = Math.Max(1, Math.Min(h, src.Height - y));

            var dst = new Screenshot { Width = w, Height = h, Bgra = new byte[w * h * 4] };
            for (int row = 0; row < h; row++)
            {
                int srcOff = ((y + row) * src.Width + x) * 4;
                int dstOff = row * w * 4;
                Buffer.BlockCopy(src.Bgra, srcOff, dst.Bgra, dstOff, w * 4);
            }
            return dst;
        }

        /// <summary>BGRA → PNG 字节。走平台层（mac 上是 ImageIO，无第三方依赖）。</summary>
        static byte[] EncodePng(Screenshot s)
        {
            string tmp = Path.Combine(Path.GetTempPath(), "remeber-crop-" + Guid.NewGuid().ToString("N") + ".png");
            try
            {
                if (!MacInterop.WritePng(tmp, s.Width, s.Height, s.Bgra))
                {
                    Program.Log("  !! WritePng 返回失败");
                    return null;
                }
                return File.ReadAllBytes(tmp);
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch (Exception) { }
            }
        }
    }
}
