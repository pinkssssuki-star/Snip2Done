using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace RemeberEr
{
    /// <summary>
    /// decay.js 的直译。刻意保持纯函数 —— 好和 JS 版一行行对照。
    /// </summary>
    public static class Decay
    {
        public const long DAY = 24L * 60 * 60 * 1000;
        /// <summary>
        /// 演示加速的倍率。挑 30000 是为了让一次完整演示落在「一分钟」这个量级：
        /// 保护期 7 天 ÷ 30000 ≈ 20 秒开始变淡，21 天 ÷ 30000 ≈ 60 秒掉进纸篓。
        /// </summary>
        public const double DEMO_TIME_SCALE = 30000.0;

        public static Dictionary<string, object> DefaultCfg()
        {
            return new Dictionary<string, object>
            {
                { "fadeStartDays", 7.0 },
                { "fadeEndDays", 21.0 },
                { "minOpacity", 0.22 },
                { "binAfterDays", 21.0 },
            };
        }

        public static Dictionary<string, object> CfgFor(Dictionary<string, object> settings)
        {
            var cfg = DefaultCfg();
            var over = J.Obj(J.Get(settings, "decay"));
            if (over != null)
                foreach (var kv in over) cfg[kv.Key] = kv.Value;
            return cfg;
        }

        public static double TimeScaleFor(Dictionary<string, object> settings)
        {
            return J.Bool(settings, "demoFastDecay", false) ? DEMO_TIME_SCALE : 1.0;
        }

        /// <summary>演示是哪一刻开始的（真实时间戳）。没在演示就是 0。</summary>
        public static double DemoStartedAtFor(Dictionary<string, object> settings)
        {
            return J.Num(settings, "demoStartedAt", 0);
        }

        /// <summary>
        /// 生效年龄（天）。
        ///
        /// scale > 1 时**不能直接拿总时长去乘** —— 那样一条一小时前放下的 Wantdo
        /// 会在演示开始的那一瞬间变成 30000 倍年龄（一小时 → 3.4 年），第一次扫描
        /// 就被塞进纸篓。看到的不是「慢慢变淡」，是一次性清空，演示根本没得看。
        ///
        /// 正确做法：演示**开始之前**积累的年龄照旧，只把**开始之后**流逝的时间加速。
        /// 于是点下去的那一刻画面不变，接下来才肉眼可见地开始褪色。
        /// </summary>
        public static double ElapsedDays(double lastTouchedAt, double now, double scale, double demoStartedAt)
        {
            if (scale <= 1 || demoStartedAt <= 0) return (now - lastTouchedAt) / DAY;
            double before = Math.Max(0, demoStartedAt - lastTouchedAt);
            double during = Math.Max(0, now - demoStartedAt);
            return (before + during * scale) / DAY;
        }

        public static double OpacityFor(Dictionary<string, object> it, double now,
                                        Dictionary<string, object> cfg, double scale, double demoStartedAt)
        {
            double d = ElapsedDays(J.Num(it, "lastTouchedAt", 0), now, scale, demoStartedAt);
            double start = J.Num(cfg, "fadeStartDays", 7);
            double end = J.Num(cfg, "fadeEndDays", 21);
            double min = J.Num(cfg, "minOpacity", 0.22);
            if (d <= start) return 1;
            if (d >= end) return min;
            double t = (d - start) / (end - start);
            return 1 - t * (1 - min);
        }

        public static bool ShouldBin(Dictionary<string, object> it, double now,
                                     Dictionary<string, object> cfg, double scale, double demoStartedAt)
        {
            return ElapsedDays(J.Num(it, "lastTouchedAt", 0), now, scale, demoStartedAt)
                   >= J.Num(cfg, "binAfterDays", 21);
        }

        public static double LifeFor(Dictionary<string, object> it, double now,
                                     Dictionary<string, object> cfg, double scale, double demoStartedAt)
        {
            double d = ElapsedDays(J.Num(it, "lastTouchedAt", 0), now, scale, demoStartedAt);
            double bin = J.Num(cfg, "binAfterDays", 21);
            if (d <= 0) return 1;
            if (d >= bin) return 0;
            return 1 - d / bin;
        }

        public static double DaysLeftFor(Dictionary<string, object> it, double now,
                                         Dictionary<string, object> cfg, double scale, double demoStartedAt)
        {
            double d = ElapsedDays(J.Num(it, "lastTouchedAt", 0), now, scale, demoStartedAt);
            return Math.Max(0, J.Num(cfg, "binAfterDays", 21) - d);
        }
    }

    /// <summary>
    /// store.js 的移植。刻意读写与 Electron 版**完全相同的 JSON 结构**，
    /// 这样两个实现可以指向同一份 data.json，衰减结果能逐字段对比。
    /// </summary>
    public class Store
    {
        public static Dictionary<string, object> DefaultSettings()
        {
            return new Dictionary<string, object>
            {
                { "demoFastDecay", false },
                { "panelOpacity", 0.8 },
                // 快速模式：框选松手就定，跳过右下角那个 ✓（原生版新增，JS 版会当成多余字段忽略）
                { "quickCapture", false },
                // 是否一直压在别的窗口上面。单屏用户不需要一直压着，所以给了开关
                { "alwaysOnTop", true },
                // 把顶栏的折叠按钮藏掉。有了侧边收起，折叠基本是多余的
                { "hideCollapseButton", false },
                // 面板背景色：white / gray / black
                { "panelTheme", "white" },
                // 临期优先：越临期越靠上，卡片越红
                { "urgentSort", true },
            };
        }

        public readonly string DataDir;
        public readonly string ImagesDir;
        readonly string _dataFile;
        Dictionary<string, object> _data;

        public Store(string dataDir)
        {
            DataDir = dataDir;
            ImagesDir = Path.Combine(dataDir, "images");
            _dataFile = Path.Combine(dataDir, "data.json");
            Directory.CreateDirectory(ImagesDir);
            _data = Load();
        }

        public Dictionary<string, object> Settings
        {
            get { return J.Obj(_data["settings"]); }
        }

        public List<object> Items
        {
            get { return J.Arr(_data["items"]); }
        }

        /// <summary>比对用：把「现在」钉死，让 C# 和 JS 两个实现能在同一时刻求值。</summary>
        public static long? NowOverride;


        public static long Now()
        {
            return NowOverride ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        /// <summary>
        /// **真实**墙上时间，不受 --now= 钉死影响。
        /// 演示的起跑时刻必须用这个：用 Now() 的话，--now= 一开就永远追不上，
        /// 演示会瞬间跑完或者永远不动。
        /// </summary>
        public static long RealNow()
        {
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        static Dictionary<string, object> NewFields()
        {
            return new Dictionary<string, object>
            {
                { "note", "" },
                { "reflection", "" },
                { "reflectionAt", null },
                { "createdAt", null },
                { "lastTouchedAt", null },
                { "touchCount", 0.0 },
                { "dueBucket", null },
            { "dueDays", null },
                { "dueAt", null },
                { "doneAt", null },
                { "filedAt", null },
                { "binnedAt", null },
                { "binCount", 0.0 },
                { "binTotalMs", 0.0 },
            };
        }

        Dictionary<string, object> Load()
        {
            try
            {
                string raw = File.ReadAllText(_dataFile);
                var parsed = J.Obj(Json.Parse(raw));
                if (parsed != null && J.Arr(J.Get(parsed, "items")) != null)
                {
                    var merged = DefaultSettings();
                    var over = J.Obj(J.Get(parsed, "settings"));
                    if (over != null) foreach (var kv in over) merged[kv.Key] = kv.Value;
                    parsed["settings"] = merged;

                    var upgraded = new List<object>();
                    foreach (var raw0 in J.Arr(J.Get(parsed, "items")))
                    {
                        var item = NewFields();
                        var src = J.Obj(raw0);
                        if (src != null) foreach (var kv in src) item[kv.Key] = kv.Value;
                        upgraded.Add(item);
                    }
                    parsed["items"] = upgraded;
                    return parsed;
                }
            }
            catch (Exception)
            {
                // 首次运行或文件损坏 → 重建
            }
            var fresh = new Dictionary<string, object>
            {
                { "version", 1.0 },
                { "items", new List<object>() },
                { "settings", DefaultSettings() },
            };
            return fresh;
        }

        public void Save()
        {
            string tmp = _dataFile + ".tmp";
            File.WriteAllText(tmp, Json.Stringify(_data), new System.Text.UTF8Encoding(false));
            if (File.Exists(_dataFile)) File.Delete(_dataFile);
            File.Move(tmp, _dataFile);
        }

        public Dictionary<string, object> SetSetting(string key, object value)
        {
            Settings[key] = value;
            Save();
            return Settings;
        }

        public string SaveImage(string id, byte[] png)
        {
            string name = id + ".png";
            File.WriteAllBytes(Path.Combine(ImagesDir, name), png);
            return "images/" + name;
        }

        public Dictionary<string, object> Add(string room, string title, string source, string imageRel, string dueBucket)
        {
            return Add(room, title, source, imageRel, dueBucket, null, 0);
        }

        /// <param name="dueDays">「两三天」那一档的自定义天数；0 = 没写，用 48~72 小时随机</param>
        public Dictionary<string, object> Add(string room, string title, string source, string imageRel,
                                              string dueBucket, string id, int dueDays = 0)
        {
            long now = Now();
            var item = NewFields();
            item["id"] = id ?? Guid.NewGuid().ToString();
            item["room"] = room;
            item["state"] = "active";
            item["title"] = title ?? "";
            item["source"] = source ?? "";
            item["image"] = imageRel;
            item["createdAt"] = (double)now;
            item["lastTouchedAt"] = (double)now;
            item["dueBucket"] = dueBucket;
            // 写了数字才记下来：标签上要显示「N 天」而不是笼统的「两三天」
            item["dueDays"] = dueDays > 0 ? (object)(double)dueDays : null;
            item["dueAt"] = dueBucket == null ? null : (object)(double)DueTimestamp(dueBucket, now, dueDays);
            Items.Add(item);
            Save();
            return item;
        }

        public Dictionary<string, object> Get(string id)
        {
            foreach (var o in Items)
            {
                var it = J.Obj(o);
                if (J.Str(it, "id", "") == id) return it;
            }
            return null;
        }

        /// <summary>新条目在最后，但 UI 要最近的在前 —— 与 JS 版相反，这里给出一个稳定排序视图。</summary>
        public Dictionary<string, object> Update(string id, Dictionary<string, object> patch)
        {
            var it = Get(id);
            if (it == null || patch == null) return null;
            if (J.Get(patch, "title") is string) it["title"] = ((string)J.Get(patch, "title")).Trim();
            if (J.Get(patch, "note") is string) it["note"] = J.Get(patch, "note");
            if (J.Get(patch, "reflection") is string)
            {
                string r = (string)J.Get(patch, "reflection");
                it["reflection"] = r;
                it["reflectionAt"] = r.Length > 0 ? (object)(double)Now() : null;
            }
            Save();
            return it;
        }

        /// <summary>
        /// 改一条的期限。
        ///
        /// 单独开一个方法，而不是把 dueAt 加进 Update() 的白名单 —— Update 只放行
        /// title / note / reflection 是**刻意**的（任何调用者都不该能写任意字段）。
        /// 顺带记一笔：我第一次就是走 Update() 的，它把 dueAt 静默丢了还照样 Save，
        /// 从日志上完全看不出问题，只有比对落库结果才发现。
        /// </summary>
        public Dictionary<string, object> SetDue(string id, double dueAt, int dueDays, string dueBucket)
        {
            var it = Get(id);
            if (it == null) return null;
            it["dueAt"] = dueAt;
            it["dueDays"] = dueDays > 0 ? (object)(double)dueDays : null;
            if (dueBucket != null) it["dueBucket"] = dueBucket;
            Save();
            return it;
        }

        public Dictionary<string, object> Touch(string id)
        {
            var it = Get(id);
            if (it == null) return null;
            it["lastTouchedAt"] = (double)Now();
            it["touchCount"] = J.Num(it, "touchCount", 0) + 1;
            Save();
            return it;
        }

        /// <summary>
        /// 把所有还在的 Wantdo 生命复原（lastTouchedAt = 现在）。
        ///
        /// 只动生命值，**不动 touchCount / viewCount** —— 那两个是「你真的看过它几次」的
        /// 记录，替用户"注意一遍"不该往里面灌水，不然完成记录里会出现一看就是假的数字。
        /// </summary>
        public int RefreshAllWantsLife()
        {
            double now = Now();
            int n = 0;
            foreach (var o in Items)
            {
                var it = J.Obj(o);
                if (J.Str(it, "room", "") != "want") continue;
                if (J.Str(it, "state", "") != "active") continue;
                it["lastTouchedAt"] = now;
                n++;
            }
            if (n > 0) Save();
            return n;
        }

        /// <summary>
        /// 把一条已完成的条目撤回成未完成（回到它原来的房间列表）。
        ///
        /// 和 Done() 里那个「再点一次撤销」不同：那个只在**还没收走**时有效；
        /// Done 列表里看到的是已经 filedAt 的，必须把 filedAt 一起清掉。
        /// 这是给「误 Done 了」准备的后悔药。
        /// </summary>
        public Dictionary<string, object> UnDone(string id)
        {
            var it = Get(id);
            if (it == null) return null;
            if (J.Str(it, "state", "") != "done") return it;

            it["state"] = "active";
            it["doneAt"] = null;
            it["filedAt"] = null;
            // 撤回之后它重新"活着"：对 Wantdo 来说，保质期从这一刻重新开始
            it["lastTouchedAt"] = (double)Now();
            Save();
            Program.Log("  撤回完成：" + J.Str(it, "title", "") + " -> room=" + J.Str(it, "room", ""));
            return it;
        }

        public Dictionary<string, object> Done(string id, bool hasReflection, string reflection, bool file)
        {
            var it = Get(id);
            if (it == null) return null;

            // 再点一次 = 撤销完成（只在还没收走时有效）
            if (J.Str(it, "state", "") == "done" && J.IsNull(J.Get(it, "filedAt")))
            {
                it["state"] = "active";
                it["doneAt"] = null;
                it["filedAt"] = null;
                Save();
                return it;
            }

            CloseBinStint(it, Now());
            it["state"] = "done";
            it["doneAt"] = (double)Now();
            if (hasReflection)
            {
                string r = (reflection ?? "").Trim();
                it["reflection"] = r;
                it["reflectionAt"] = r.Length > 0 ? (object)(double)Now() : null;
            }
            if (file) it["filedAt"] = (double)Now();
            Save();
            return it;
        }

        public Dictionary<string, object> File_(string id)
        {
            var it = Get(id);
            if (it == null || J.Str(it, "state", "") != "done") return null;
            if (J.IsNull(J.Get(it, "filedAt"))) it["filedAt"] = (double)Now();
            Save();
            return it;
        }

        public Dictionary<string, object> Bin(string id)
        {
            var it = Get(id);
            if (it == null) return null;
            if (J.Str(it, "state", "") != "binned")
            {
                it["state"] = "binned";
                it["binnedAt"] = (double)Now();
                it["binCount"] = J.Num(it, "binCount", 0) + 1;
                Save();
            }
            return it;
        }

        public Dictionary<string, object> Restore(string id)
        {
            var it = Get(id);
            if (it == null) return null;
            CloseBinStint(it, Now());
            it["state"] = "active";
            it["lastTouchedAt"] = (double)Now();
            Save();
            return it;
        }

        void CloseBinStint(Dictionary<string, object> it, long now)
        {
            object binnedAt = J.Get(it, "binnedAt");
            if (binnedAt is double)
            {
                it["binTotalMs"] = J.Num(it, "binTotalMs", 0) + Math.Max(0, now - (double)binnedAt);
            }
            it["binnedAt"] = null;
        }

        public bool Remove(string id)
        {
            for (int i = 0; i < Items.Count; i++)
            {
                var it = J.Obj(Items[i]);
                if (J.Str(it, "id", "") != id) continue;
                string img = J.Str(it, "image", null);
                if (!string.IsNullOrEmpty(img))
                {
                    string abs = Path.Combine(DataDir, img.Replace('/', Path.DirectorySeparatorChar));
                    try { File.Delete(abs); }
                    catch (Exception) { }
                    // 原生版会在旁边存一份 <id>.thumb.png（WPF 的 PNG 解码不做缩放解码，
                    // 不缓存缩略图的话每次启动都要把 7MB 的截图整张解一遍）
                    try
                    {
                        string thumb = Path.Combine(Path.GetDirectoryName(abs),
                            Path.GetFileNameWithoutExtension(abs) + ".thumb.png");
                        if (File.Exists(thumb)) File.Delete(thumb);
                    }
                    catch (Exception) { }
                }
                Items.RemoveAt(i);
                Save();
                return true;
            }
            return false;
        }

        /// <summary>
        /// 首次使用的演示数据。原版 devseed.js 会手搓 PNG；这里只落文字条目，
        /// 因为本次要比的是「HTML UI 在 WebView2 里还原得怎么样」，不是造数据。
        /// </summary>
        public void SeedDemo()
        {
            if (Items.Count > 0) return;
            long now = Now();

            Add("todo", "把课程设计的数据跑完，周四要交", "Code", null, "soon");
            Add("todo", "回 Marc 那条关于定价页文案的消息", "Slack", null, "today");
            Add("want", "想学一下 Rust，做个小工具", "抖音", null, null);
            Add("want", "把那个播客听完", "小宇宙", null, null);
            Add("want", "研究一下怎么给房间加个氛围灯", "小红书", null, null);

            // 手工把「注意力时间」往回拨，让衰减一眼可见：一个 12 天前、一个 19 天前
            var wants = new List<Dictionary<string, object>>();
            foreach (var o in Items)
            {
                var it = J.Obj(o);
                if (J.Str(it, "room", "") == "want") wants.Add(it);
            }
            if (wants.Count >= 3)
            {
                wants[1]["lastTouchedAt"] = (double)(now - 12L * Decay.DAY);
                wants[2]["lastTouchedAt"] = (double)(now - 19L * Decay.DAY);
            }

            Add("todo", "（已完成）整理上周的会议纪要", "飞书", null, null);
            var doneIt = J.Obj(Items[Items.Count - 1]);
            doneIt["state"] = "done";
            doneIt["doneAt"] = (double)(now - 2L * Decay.DAY);
            doneIt["filedAt"] = (double)(now - 2L * Decay.DAY);

            Save();
        }

        public int Sweep()
        {
            var cfg = Decay.CfgFor(Settings);
            double scale = Decay.TimeScaleFor(Settings);
            double demoStart = Decay.DemoStartedAtFor(Settings);
            double now = Now();
            int binned = 0;
            foreach (var o in Items)
            {
                var it = J.Obj(o);
                if (J.Str(it, "room", "") != "want" || J.Str(it, "state", "") != "active") continue;
                if (Decay.ShouldBin(it, now, cfg, scale, demoStart))
                {
                    it["state"] = "binned";
                    it["binnedAt"] = (double)now;
                    it["binCount"] = J.Num(it, "binCount", 0) + 1;
                    binned++;
                }
            }
            if (binned > 0) Save();
            return binned;
        }

        public Dictionary<string, object> Snapshot()
        {
            Sweep();
            var cfg = Decay.CfgFor(Settings);
            double scale = Decay.TimeScaleFor(Settings);
            double demoStart = Decay.DemoStartedAtFor(Settings);
            double now = Now();

            var items = new List<object>();
            int doneThisWeek = 0, rescued = 0;

            foreach (var o in Items)
            {
                var it = J.Obj(o);
                bool isWant = J.Str(it, "room", "") == "want";
                bool isActive = J.Str(it, "state", "") == "active";

                double binTotal = J.Num(it, "binTotalMs", 0);
                object binnedAt = J.Get(it, "binnedAt");
                double binMs = binTotal + (binnedAt is double ? Math.Max(0, now - (double)binnedAt) : 0);

                object doneAt = J.Get(it, "doneAt");
                double elapsedMs = (doneAt is double ? (double)doneAt : now) - J.Num(it, "createdAt", now);

                var outItem = new Dictionary<string, object>();
                foreach (var kv in it) outItem[kv.Key] = kv.Value;

                outItem["opacity"] = isWant && isActive ? Decay.OpacityFor(it, now, cfg, scale, demoStart) : 1.0;
                outItem["life"] = isWant && isActive ? Decay.LifeFor(it, now, cfg, scale, demoStart) : 1.0;
                outItem["daysLeft"] = isWant && isActive ? (object)Decay.DaysLeftFor(it, now, cfg, scale, demoStart) : null;

                object dueAt = J.Get(it, "dueAt");
                // 照抄 store.js 那句 `... && it.dueAt && it.dueAt < now` 的**短路行为**：
                // 条件都成立但 dueAt 是假值（手动记一笔的条目 dueAt 就是 null）时，
                // JS 那个表达式的值是 dueAt 本身，不是 false。
                // 功能上等价（两边 UI 都当布尔用），但写成严格布尔就会让
                // verify-snapshot 报「JS=null CS=false」—— 那条「逐字段可对比」的
                // 不变量比这点洁癖值钱，所以刻意保留这个怪癖，别去"顺手修好"。
                if (J.Str(it, "room", "") == "todo" && isActive)
                    outItem["overdue"] = dueAt is double ? (object)((double)dueAt < now) : dueAt;
                else
                    outItem["overdue"] = false;

                // 临期程度 0~1 —— 决定卡片「红到什么程度」。
                // 按**相对时间**算：在自己「创建 → 到期」这一段里已经走掉了多少。
                // 所以 1 天的任务走到 80%、和 10 天的任务走到 80%，红得一样多 ——
                // 它衡量的是「这件事的余量还剩几成」，不是「还剩几小时」。
                double urg = 0;
                if (J.Str(it, "room", "") == "todo" && isActive && dueAt is double)
                {
                    double due = (double)dueAt;
                    double created = J.Num(it, "createdAt", now);
                    if (now >= due) urg = 1;
                    else if (due > created) urg = Math.Max(0, Math.Min(1, (now - created) / (due - created)));
                }
                outItem["urgency"] = urg;

                outItem["binMs"] = binMs;
                outItem["binCount"] = J.Num(it, "binCount", 0);
                outItem["elapsedMs"] = elapsedMs;
                outItem["reflectionChars"] = J.Str(it, "reflection", "").Length;
                outItem["doneHour"] = doneAt is double
                    ? (object)(double)DateTimeOffset.FromUnixTimeMilliseconds((long)(double)doneAt).ToLocalTime().Hour
                    : null;

                items.Add(outItem);

                if (J.Str(it, "state", "") == "done" && doneAt is double && now - (double)doneAt < 7 * Decay.DAY) doneThisWeek++;
                if (isActive && isWant && J.Num(it, "binCount", 0) > 0) rescued++;
            }

            var stats = new Dictionary<string, object>
            {
                { "doneThisWeek", (double)doneThisWeek },
                { "rescued", (double)rescued },
            };

            return new Dictionary<string, object>
            {
                { "items", items },
                { "settings", Settings },
                { "decayCfg", cfg },
                { "stats", stats },
                { "now", now },
            };
        }

        static readonly Random _dueRnd = new Random();

        /// <summary>
        /// 期限时间戳。
        ///
        /// 「两三天」这一档：**直接点是 48~72 小时里随机一个**（每次不一样，
        /// 免得所有 Todo 都堆在同一天到期）；在旁边写了数字 N 就是 N 天后。
        /// </summary>
        static long DueTimestamp(string bucket, long from, int dueDays = 0)
        {
            if (bucket == "today")
            {
                // 对齐 JS 的 d.setHours(23,59,59,999) —— 本地时区的当天最后一毫秒
                var d = DateTimeOffset.FromUnixTimeMilliseconds(from).ToLocalTime();
                var end = new DateTimeOffset(d.Year, d.Month, d.Day, 23, 59, 59, 999, d.Offset);
                return end.ToUnixTimeMilliseconds();
            }
            if (dueDays > 0) return from + (long)dueDays * Decay.DAY;
            // 48~72 小时
            return from + _dueRnd.Next(48, 73) * 3600L * 1000L;
        }
    }
}
