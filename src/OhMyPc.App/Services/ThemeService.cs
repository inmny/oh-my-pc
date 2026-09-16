using System.Windows;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.Themes;

namespace OhMyPc.App.Services;

/// <summary>
/// 主题服务：按 id 切换全局调色字典（Themes/Palettes/*.xaml，键集合一致），
/// 并广播变更供图表绘制、窗口标题栏等代码侧消费方重取颜色。
/// </summary>
public sealed class ThemeService
{
    public const string DefaultTheme = "omp-dark";

    private ResourceDictionary? _active;

    /// <summary>当前主题 id。</summary>
    public string Current { get; private set; } = DefaultTheme;

    /// <summary>当前是否为浅色主题（决定 LiveCharts 默认主题与窗口标题栏形态）。</summary>
    public bool IsLight { get; private set; }

    public event EventHandler? ThemeChanged;

    /// <summary>可选主题 id 与界面名称的资源键。</summary>
    public static IReadOnlyList<(string Id, string NameKey)> All { get; } =
    [
        ("omp-dark", "Settings_ThemeOmpDark"),
        ("github-light", "Settings_ThemeGitHubLight"),
        ("github-dark", "Settings_ThemeGitHubDark"),
        ("one-light", "Settings_ThemeOneLight"),
        ("one-dark", "Settings_ThemeOneDark"),
        ("claude-light", "Settings_ThemeClaudeLight"),
        ("claude-dark", "Settings_ThemeClaudeDark")
    ];

    /// <summary>未知或旧版主题 id 归一到默认主题。</summary>
    public static string Normalize(string themeId) =>
        All.Any(theme => theme.Id == themeId) ? themeId : DefaultTheme;

    public void Apply(string themeId)
    {
        var id = Normalize(themeId);
        if (id == Current && _active is not null) return;
        var dictionaries = System.Windows.Application.Current.Resources.MergedDictionaries;
        // 本地化服务同样按 Source 定位字典：调色板以 Palettes/ 目录标识
        _active ??= dictionaries.FirstOrDefault(dictionary =>
            dictionary.Source?.OriginalString.Contains("Palettes/", StringComparison.Ordinal) == true);
        if (_active is not null) dictionaries.Remove(_active);
        _active = new ResourceDictionary { Source = new Uri($"Themes/Palettes/{FileName(id)}.xaml", UriKind.Relative) };
        dictionaries.Add(_active);
        Current = id;
        IsLight = id.EndsWith("light", StringComparison.Ordinal);
        LiveCharts.Configure(config => config.AddDefaultTheme(
            requestedTheme: IsLight ? LvcThemeKind.Light : LvcThemeKind.Dark));
        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>读取当前调色板画刷的颜色，供 SkiaSharp 图表绘制使用；缺失时品红便于暴露问题。</summary>
    public System.Windows.Media.Color GetColor(string brushKey) =>
        System.Windows.Application.Current.TryFindResource(brushKey) is System.Windows.Media.SolidColorBrush { Color: var color }
            ? color
            : System.Windows.Media.Colors.Magenta;

    private static string FileName(string id) => id switch
    {
        "github-light" => "GithubLight",
        "github-dark" => "GithubDark",
        "one-light" => "OneLight",
        "one-dark" => "OneDark",
        "claude-light" => "ClaudeLight",
        "claude-dark" => "ClaudeDark",
        _ => "OhMyPcDark"
    };
}
