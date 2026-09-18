using System;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace RemeberEr
{
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

    /// <summary>够用就好的 ICommand —— 不引第三方 MVVM 库，省体积也省依赖。</summary>
    public class RelayCommand : System.Windows.Input.ICommand
    {
        readonly Action<object> _exec;
        readonly Func<object, bool> _can;

        public RelayCommand(Action<object> exec, Func<object, bool> can = null)
        {
            _exec = exec;
            _can = can;
        }

        public RelayCommand(Action exec) : this(_ => exec()) { }

        public event EventHandler CanExecuteChanged
        {
            add { System.Windows.Input.CommandManager.RequerySuggested += value; }
            remove { System.Windows.Input.CommandManager.RequerySuggested -= value; }
        }

        public bool CanExecute(object p) { return _can == null || _can(p); }
        public void Execute(object p) { _exec(p); }
    }

    // ------------------------------------------------------------------ 转换器

    /// <summary>
    /// DPI 换算。Win32 给的都是物理像素，而 WPF 的 Window.Left/Top 要 DIP，
    /// 混用就会把窗口放到错的地方 —— 之前在 WebView2 原型上踩过一次。
    /// </summary>
    public static class Dpi
    {
        public static void Scale(Visual v, out double sx, out double sy)
        {
            sx = 1; sy = 1;
            var src = PresentationSource.FromVisual(v);
            if (src != null && src.CompositionTarget != null)
            {
                var m = src.CompositionTarget.TransformToDevice;
                sx = m.M11;
                sy = m.M22;
            }
        }
    }

    /// <summary>bool → Visibility，ConverterParameter=invert 可反相。</summary>
    public class BoolToVis : IValueConverter
    {
        public object Convert(object v, Type t, object p, CultureInfo c)
        {
            bool b = v is bool && (bool)v;
            if (p as string == "invert") b = !b;
            return b ? Visibility.Visible : Visibility.Collapsed;
        }
        public object ConvertBack(object v, Type t, object p, CultureInfo c) { throw new NotSupportedException(); }
    }

    /// <summary>空字符串 / null → Collapsed。</summary>
    public class EmptyToVis : IValueConverter
    {
        public object Convert(object v, Type t, object p, CultureInfo c)
        {
            bool has = !string.IsNullOrWhiteSpace(v as string);
            if (p as string == "empty") has = !has;
            return has ? Visibility.Visible : Visibility.Collapsed;
        }
        public object ConvertBack(object v, Type t, object p, CultureInfo c) { throw new NotSupportedException(); }
    }

    /// <summary>bool → 取反。给 IsChecked / IsEnabled 这类用。</summary>
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

    /// <summary>0~1 或 0~100 的比例 → 0~100，喂给 ProgressBar 的生命条。</summary>
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
