using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace RemeberEr
{
    /// <summary>
    /// 面板配色（白 / 灰 / 黑三个主题）。
    ///
    /// 只有「面板自己」的颜色跟着主题走；**条目卡片永远是白底黑字**，
    /// 这是设计原则第 5 条（卡片必须永远清晰可读），所以 Cx1 / Cx2 / CardBg
    /// 不在这里面，它们在任何主题下都不变。
    ///
    /// 实现方式与原版（WPF）一致，原因值得记一笔：
    ///   XAML 里全是 <c>{StaticResource Tx2}</c> 这种写法，StaticResource 是
    ///   **加载时解析一次**，按理说换主题得重载整棵视觉树。
    ///   但解析出来的是同一个 SolidColorBrush **实例** —— 只要它没被冻结，
    ///   直接改它的 Color，所有引用处会一起跟着变。
    ///
    ///   Avalonia 的 StaticResource 语义相同（同样返回同一个实例），
    ///   而且 Avalonia 的 IBrush 没有 WPF 的 Freeze 概念、天然可变，
    ///   所以这个办法在 Avalonia 上照样成立 —— 不需要改成 DynamicResource。
    ///
    /// 落地时要保证：Theme.axaml 里这些 key 定义出来的画刷实例，
    /// 必须是这里 Apply 时改到的那一个。做法是按 key 从应用资源里查出来再改
    /// （见 Apply），而不是另造一批画刷 —— 另造就不是同一个实例了。
    /// </summary>
    public static class Palette
    {
        public class Entry
        {
            public string Key;          // 存进 settings 的值
            public string Name;         // 设置里显示的名字
            public Color Panel;         // 面板底色（不含 alpha，alpha 由透明度设置决定）
            public Color Surface;       // 设置页那种完全不透明的底
            public Color Tx1, Tx2, Tx3, Tx4, Tx5, TxFaint;
            public Color Hair;          // 分隔线
            public Color Chip;          // 小徽标的底
            public Color Hint;          // 占位/提示文字
            public Color Red;
            public Color BtnBg, BtnBgHover, BtnBorder;
            public Color InputBg, TrackBg, ThumbBg;
            public Color EmptyBg, EmptyBorder, HoverBg;
            public Color SwatchRing, RoomBorder;
        }

        static Color C(string hex)
        {
            // 原版是 WPF 的 ColorConverter；Avalonia.Media.Color.Parse 等价
            return Color.Parse(hex);
        }

        public static readonly List<Entry> All = new List<Entry>
        {
            new Entry
            {
                Key = "white", Name = "白",
                Panel = C("#FFFFFF"), Surface = C("#FFFFFF"),
                Tx1 = C("#1c1c1e"), Tx2 = C("#2c2c2e"), Tx3 = C("#7a7a80"),
                Tx4 = C("#86868c"), Tx5 = C("#a1a1a6"), TxFaint = C("#c4c4c9"),
                Hair = C("#12000000"), Chip = C("#12000000"), Hint = C("#9a9aa0"),
                Red = C("#dc2626"),
                BtnBg = C("#b8ffffff"), BtnBgHover = C("#ffffff"), BtnBorder = C("#1a000000"),
                InputBg = C("#ffffff"), TrackBg = C("#21000000"), ThumbBg = C("#ffffff"),
                EmptyBg = C("#80ffffff"), EmptyBorder = C("#1f000000"), HoverBg = C("#14000000"),
                SwatchRing = C("#33000000"), RoomBorder = C("#33000000"),
            },
            new Entry
            {
                Key = "gray", Name = "灰",
                Panel = C("#E9E9EC"), Surface = C("#E9E9EC"),
                Tx1 = C("#1c1c1e"), Tx2 = C("#28282b"), Tx3 = C("#6e6e75"),
                Tx4 = C("#77777e"), Tx5 = C("#909098"), TxFaint = C("#b4b4bb"),
                Hair = C("#18000000"), Chip = C("#18000000"), Hint = C("#84848b"),
                Red = C("#cc2222"),
                BtnBg = C("#ccffffff"), BtnBgHover = C("#ffffff"), BtnBorder = C("#22000000"),
                InputBg = C("#f7f7f9"), TrackBg = C("#26000000"), ThumbBg = C("#ffffff"),
                EmptyBg = C("#8cffffff"), EmptyBorder = C("#22000000"), HoverBg = C("#18000000"),
                SwatchRing = C("#40000000"), RoomBorder = C("#40000000"),
            },
            new Entry
            {
                Key = "black", Name = "黑",
                Panel = C("#1B1B1F"), Surface = C("#1B1B1F"),
                Tx1 = C("#F4F4F6"), Tx2 = C("#E6E6EA"), Tx3 = C("#A6A6AE"),
                Tx4 = C("#96969E"), Tx5 = C("#7E7E86"), TxFaint = C("#5a5a62"),
                Hair = C("#1FFFFFFF"), Chip = C("#1FFFFFFF"), Hint = C("#8a8a92"),
                Red = C("#f87171"),
                BtnBg = C("#1FFFFFFF"), BtnBgHover = C("#2EFFFFFF"), BtnBorder = C("#26FFFFFF"),
                InputBg = C("#24242A"), TrackBg = C("#33FFFFFF"), ThumbBg = C("#E8E8EC"),
                EmptyBg = C("#12FFFFFF"), EmptyBorder = C("#22FFFFFF"), HoverBg = C("#1AFFFFFF"),
                SwatchRing = C("#70FFFFFF"), RoomBorder = C("#40FFFFFF"),
            },
        };

        public static Entry Find(string key)
        {
            foreach (var e in All) if (e.Key == key) return e;
            return All[0];
        }

        public static Entry Current = All[0];

        /// <summary>
        /// 把某个主题应用到当前界面。
        ///
        /// 关键点：改的是**应用资源里已有的那些画刷实例**，不是新建一批。
        /// 新建的话引用处还指着旧实例，界面不会变 —— 这一步是整个机制的命门。
        /// </summary>
        public static void Apply(string key)
        {
            var e = Find(key);
            Current = e;

            var app = Application.Current;
            if (app == null) return;

            // key -> 该主题下的颜色。Theme.axaml 里必须有同名 key，否则这条会静默跳过 ——
            // 所以 Set 里对「找不到」是记日志而不是抛异常：少一个 token 不该让换主题崩掉。
            var map = new Dictionary<string, Color>
            {
                { "Tx1", e.Tx1 }, { "Tx2", e.Tx2 }, { "Tx3", e.Tx3 },
                { "Tx4", e.Tx4 }, { "Tx5", e.Tx5 }, { "TxFaint", e.TxFaint },
                { "Hair", e.Hair }, { "ChipBg", e.Chip }, { "HintTx", e.Hint },
                { "Red", e.Red },
                { "SurfaceBg", e.Surface },
                { "BtnBg", e.BtnBg }, { "BtnBgHover", e.BtnBgHover }, { "BtnBorder", e.BtnBorder },
                { "InputBg", e.InputBg }, { "TrackBg", e.TrackBg }, { "ThumbBg", e.ThumbBg },
                { "EmptyBg", e.EmptyBg }, { "EmptyBorder", e.EmptyBorder }, { "HoverBg", e.HoverBg },
                { "SwatchRing", e.SwatchRing }, { "RoomBorder", e.RoomBorder },
            };

            foreach (var kv in map) Set(app, kv.Key, kv.Value);

            // 主题标识：XAML 里可以用 {DynamicResource PanelThemeKey} 之类的地方需要它
            // （目前没有，但留着比以后临时加好）
            PaletteApplied?.Invoke(e);
        }

        /// <summary>主题换完之后的通知。窗口若需要额外动作可以挂这里。</summary>
        public static event Action<Entry> PaletteApplied;

        static void Set(Application app, string key, Color c)
        {
            try
            {
                object cur;
                if (!app.TryFindResource(key, out cur)) return;   // Theme.axaml 里没这个 token
                var brush = cur as SolidColorBrush;
                if (brush == null) return;

                // 原地改颜色 —— 这就是「改一个实例、所有引用处一起变」那一步。
                // Avalonia 的 SolidColorBrush 没有 Freeze，改完会自动触发重绘。
                brush.Color = c;
            }
            catch (Exception ex)
            {
                Program.Log("  !! Palette 设置 " + key + " 失败：" + ex.Message);
            }
        }
    }
}
