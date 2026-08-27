using System.Windows;
using System.Windows.Controls;

namespace OhMyPc.App.Dialogs;

/// <summary>各代理对话框共用的复选组构建/收集。</summary>
internal static class ProxyDialogChecks
{
    public static void Build(WrapPanel panel, IReadOnlyList<string> options, IEnumerable<string>? selected)
    {
        List<string> chosen = selected is null ? [] : [.. selected];
        foreach (var option in options)
        {
            panel.Children.Add(new System.Windows.Controls.CheckBox
            {
                Content = option,
                IsChecked = chosen.Contains(option),
                Margin = new Thickness(0, 0, 18, 6),
                MinWidth = 72
            });
        }
    }

    public static List<string> Collect(WrapPanel panel, IReadOnlyList<string> options) =>
        [.. options.Where(option => panel.Children.OfType<System.Windows.Controls.CheckBox>().Any(box => Equals(box.Content, option) && box.IsChecked == true))];
}
