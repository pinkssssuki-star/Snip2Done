using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace RemeberEr
{
    /// <summary>
    /// 只为验证「StaticResource 与 DynamicResource 在运行期改画笔颜色时会不会跟着变」。
    /// 这不是产品界面的一部分，是移植期的一次性探针 —— 结论见 Palette.cs 顶部说明。
    /// </summary>
    public partial class ResourceProbe : UserControl
    {
        public ResourceProbe() { AvaloniaXamlLoader.Load(this); }

        public string Report()
        {
            var s = this.FindControl<TextBlock>("Stat");
            var d = this.FindControl<TextBlock>("Dyn");
            var sb = s?.Foreground as Avalonia.Media.SolidColorBrush;
            var db = d?.Foreground as Avalonia.Media.SolidColorBrush;
            return string.Format("Static={0} Dynamic={1}",
                sb == null ? "null" : sb.Color.ToString(),
                db == null ? "null" : db.Color.ToString());
        }
    }
}
