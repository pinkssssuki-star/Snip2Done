using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Input;
using Avalonia.Data.Converters;
using Avalonia.Threading;

namespace RemeberEr
{
    // ------------------------------------------------------------------ MVVM 基类

    /// <summary>属性变更通知基类。与 WPF 版行为一致，只换了命名空间。</summary>
    public abstract class ObservableObject : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        protected void Raise(string name)
        {
            var h = PropertyChanged;
            if (h != null) h(this, new PropertyChangedEventArgs(name));
        }

        protected bool Set<T>(ref T field, T value, string name)
        {
            if (Equals(field, value)) return false;
            field = value;
            Raise(name);
            return true;
        }
    }

    /// <summary>
    /// 够用就好的 ICommand —— 和 WPF 版一样不引第三方 MVVM 库。
    ///
    /// 差异说明：WPF 的 CanExecuteChanged 可以挂在 CommandManager.RequerySuggested 上，
    /// 由 WPF 自己在输入事件后统一重算；Avalonia 没有 CommandManager。
    /// 这里改成维护一份实例登记表，由 UI 需要时主动 RefreshAll() 触发重算 ——
    /// 语义上等价（按钮可用性随状态更新），但触发点由我们控制，行为更可预测。
    /// </summary>
    public class RelayCommand : ICommand
    {
        readonly Action<object> _exec;
        readonly Func<object, bool> _can;

        static readonly List<RelayCommand> _all = new List<RelayCommand>();

        public RelayCommand(Action<object> exec, Func<object, bool> can = null)
        {
            _exec = exec;
            _can = can;
            lock (_all) { _all.Add(this); }
        }

        public RelayCommand(Action exec) : this(_ => exec()) { }

        public event EventHandler CanExecuteChanged;

        public bool CanExecute(object p) { return _can == null || _can(p); }
        public void Execute(object p) { _exec(p); }

        /// <summary>所有命令重算可用性。UI 状态变化后调用。</summary>
        public static void RefreshAll()
        {
            RelayCommand[] copy;
            lock (_all) { copy = _all.ToArray(); }
            foreach (var c in copy) c.Raise();
        }

        void Raise()
        {
            var h = CanExecuteChanged;
            if (h == null) return;
            // 绑定系统要求在 UI 线程上发生变更通知
            if (Dispatcher.UIThread.CheckAccess()) h(this, EventArgs.Empty);
            else Dispatcher.UIThread.Post(() => h(this, EventArgs.Empty));
        }
    }

    // ------------------------------------------------------------------ 转换器

    /// <summary>
    /// 逻辑像素 ↔ 物理像素换算。
    ///
    /// WPF 版从 PresentationSource.TransformToDevice 取缩放；Avalonia 用
    /// TopLevel.RenderScaling（每个 TopLevel 各自一份）。抓屏给的是物理像素，
    /// 而窗口坐标是逻辑像素，混用就会把窗口/选区放到错的地方 ——
    /// 原项目在 WebView2 原型上为此踩过一次坑，这里保持「先换算再用」的纪律。
    /// </summary>
    public static class Dpi
    {
        /// <summary>取某个 TopLevel 的缩放系数（Retina 屏通常是 2.0）。</summary>
        public static void Scale(Avalonia.Controls.TopLevel tl, out double sx, out double sy)
        {
            sx = 1; sy = 1;
            if (tl != null && tl.RenderScaling > 0)
            {
                sx = tl.RenderScaling;
                sy = tl.RenderScaling;
            }
        }

        /// <summary>把物理像素矩形换算成逻辑像素矩形。</summary>
        public static Avalonia.Rect PhysicalToLogical(Avalonia.Controls.TopLevel tl, double x, double y, double w, double h)
        {
            double sx, sy;
            Scale(tl, out sx, out sy);
            return new Avalonia.Rect(x / sx, y / sy, w / sx, h / sy);
        }
    }

    /// <summary>bool → 可见性，ConverterParameter=invert 可反相。</summary>
    public class BoolToVis : IValueConverter
    {
        public object Convert(object v, Type t, object p, CultureInfo c)
        {
            bool b = v is bool && (bool)v;
            if (p as string == "invert") b = !b;
            return b;
        }
        public object ConvertBack(object v, Type t, object p, CultureInfo c) { throw new NotSupportedException(); }
    }

    /// <summary>空字符串 / null → 不可见。</summary>
    public class EmptyToVis : IValueConverter
    {
        public object Convert(object v, Type t, object p, CultureInfo c)
        {
            bool has = !string.IsNullOrWhiteSpace(v as string);
            if (p as string == "empty") has = !has;
            return has;
        }
        public object ConvertBack(object v, Type t, object p, CultureInfo c) { throw new NotSupportedException(); }
    }

    /// <summary>bool → 取反。</summary>
    public class Not : IValueConverter
    {
        public object Convert(object v, Type t, object p, CultureInfo c) { return !(v is bool && (bool)v); }
        public object ConvertBack(object v, Type t, object p, CultureInfo c) { return !(v is bool && (bool)v); }
    }

    /// <summary>
    /// 没有截图时用标题首字做占位块 —— 和 HTML 版 firstChar() 的取法一致，
    /// 用 StringInfo 按字素取，避免把 emoji 劈成半个。
    /// </summary>
    public class FirstChar : IValueConverter
    {
        public object Convert(object v, Type t, object p, CultureInfo c)
        {
            var s = (v as string ?? "").Trim();
            if (s.Length == 0) return "·";
            var e = StringInfo.GetTextElementEnumerator(s);
            return e.MoveNext() ? e.GetTextElement() : "·";
        }
        public object ConvertBack(object v, Type t, object p, CultureInfo c) { throw new NotSupportedException(); }
    }

    /// <summary>bool → "on" / null。给按钮的 Tag 用，模板里靠 Tag 切高亮态。</summary>
    public class TagOn : IValueConverter
    {
        public object Convert(object v, Type t, object p, CultureInfo c)
        {
            return (v is bool && (bool)v) ? "on" : null;
        }
        public object ConvertBack(object v, Type t, object p, CultureInfo c) { throw new NotSupportedException(); }
    }

    /// <summary>
    /// 把一个字符串值原样透出，专门给 <c>Classes.xxx="{Binding ...}"</c> 用。
    /// 配合 Avalonia 的类选择器（如 <c>Border.Pill.today</c>）实现
    /// WPF 版 DataTrigger 那种「按数据切样式」的效果。
    /// </summary>
    public class AsIs : IValueConverter
    {
        public object Convert(object v, Type t, object p, CultureInfo c) { return v; }
        public object ConvertBack(object v, Type t, object p, CultureInfo c) { return v; }
    }

    /// <summary>
    /// 生命值 → 样式类：正常 "" / 偏低 "low" / 危急 "critical"。
    /// 阈值和 panel.js 里生命条的配色分档一致。
    /// </summary>
    public class LifeClass : IValueConverter
    {
        public object Convert(object v, Type t, object p, CultureInfo c)
        {
            double d = v is double ? (double)v : 100.0;
            if (d <= 15) return "critical";
            if (d <= 40) return "low";
            return "";
        }
        public object ConvertBack(object v, Type t, object p, CultureInfo c) { throw new NotSupportedException(); }
    }

    /// <summary>
    /// 0~1 或 0~100 的比例 → 0~100，喂给生命条。
    /// </summary>
    public class RatioToPercent : IValueConverter
    {
        public object Convert(object v, Type t, object p, CultureInfo c)
        {
            double d = v is double ? (double)v : 0;
            if (d <= 1.0001) d *= 100;
            return Math.Max(0, Math.Min(100, d));
        }
        public object ConvertBack(object v, Type t, object p, CultureInfo c) { throw new NotSupportedException(); }
    }
}
