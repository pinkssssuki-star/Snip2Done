using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace RemeberEr
{
    /// <summary>
    /// 面板配色。
    ///
    /// 只有「面板自己」的颜色跟着主题走；**条目卡片永远是白底黑字**，
    /// 这是设计原则第 5 条（卡片必须永远清晰可读），所以 Cx1 / Cx2 / CardBg
    /// 这几个不在这里面，它们在任何主题下都不变。
    ///
    /// 实现的取巧之处：XAML 里全用 {StaticResource Tx2} 这种写法，
    /// StaticResource 是**加载时解析一次**，按理说换主题得重载整棵树。
    /// 但解析出来的是同一个 SolidColorBrush **实例** —— 只要它没被冻结，
    /// 直接改它的 Color，所有引用处会一起跟着变。所以这里就是改这几个画笔的颜色。
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
            public Color Chip;          // 小徽标的底（TODO 后面那个数字）
            public Color Hint;          // 说明文字（原来是写死的 #9a9aa0）
            public Color Red;           // 逾期/危险
            // 控件表面：不跟主题走的话，黑色主题下按钮会变成一块块白药丸
            public Color BtnBg, BtnBgHover, BtnBorder, InputBg;
            public Color TrackBg, ThumbBg, EmptyBg, EmptyBorder, HoverBg;
        }

        static Color C(string hex)
        {
            return (Color)ColorConverter.ConvertFromString(hex);
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
            },
        };

        public static Entry Find(string key)
        {
            foreach (var e in All) if (e.Key == key) return e;
            return All[0];
        }

        /// <summary>当前主题（供 PanelBackground 取底色用）。</summary>
        public static Entry Current = All[0];

        /// <summary>
        /// 换主题：**替换**这几个资源对象，而不是改它们的颜色。
        ///
        /// 一开始想的是「StaticResource 解析出来是同一个画笔实例，直接改它的 Color
        /// 就能让所有引用处一起变」—— 但 XAML 里声明的 SolidColorBrush 会被 WPF
        /// **自动冻结**（实测 IsFrozen=True），改不动，而且改不动是静默的。
        ///
        /// 所以改成：XAML 里这几个键一律用 {DynamicResource}（查找时机在运行时，
        /// 会跟着资源表的变化走），这里往 Application.Resources 里塞同名的新画笔。
        /// ResourceDictionary 的直接项优先于它的 MergedDictionaries，正好能盖掉原来的。
        /// </summary>
        public static void Apply(string key)
        {
            var e = Find(key);
            Current = e;
            Set("Tx1", e.Tx1); Set("Tx2", e.Tx2); Set("Tx3", e.Tx3);
            Set("Tx4", e.Tx4); Set("Tx5", e.Tx5); Set("TxFaint", e.TxFaint);
            Set("Hair", e.Hair); Set("SurfaceBg", e.Surface);
            Set("ChipBg", e.Chip); Set("HintTx", e.Hint); Set("Red", e.Red);
            Set("BtnBg", e.BtnBg); Set("BtnBgHover", e.BtnBgHover); Set("BtnBorder", e.BtnBorder);
            Set("InputBg", e.InputBg); Set("TrackBg", e.TrackBg); Set("ThumbBg", e.ThumbBg);
            Set("TabOnBg", e.BtnBgHover);
            Set("EmptyBg", e.EmptyBg); Set("EmptyBorder", e.EmptyBorder); Set("HoverBg", e.HoverBg);
        }

        static void Set(string resKey, Color c)
        {
            try
            {
                var app = Application.Current;
                if (app == null) return;
                var b = new SolidColorBrush(c);
                b.Freeze();          // 新画笔不改了，冻上省点心
                app.Resources[resKey] = b;
            }
            catch (Exception ex)
            {
                Program.Log("  !! 换主题失败 " + resKey + "：" + ex.Message);
            }
        }
    }
}
