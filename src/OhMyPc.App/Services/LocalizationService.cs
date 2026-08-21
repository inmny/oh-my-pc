using System.ComponentModel;
using System.Globalization;
using System.Windows;
using OhMyPc.Core;
using OhMyPc.Core.Domain;

namespace OhMyPc.App.Services;

public sealed class LocalizationService : ITextLocalizer, INotifyPropertyChanged
{
    public const string DefaultLanguage = "zh-CN";
    private ResourceDictionary? _activeDictionary;

    public event EventHandler? LanguageChanged;
    public event PropertyChangedEventHandler? PropertyChanged;
    public string CurrentLanguage { get; private set; } = DefaultLanguage;

    public string this[string key] => System.Windows.Application.Current.TryFindResource(key) as string ?? key;

    public string Format(string key, params object?[] arguments) =>
        string.Format(CultureInfo.CurrentCulture, this[key], arguments);

    public string GetEnum(Enum value)
    {
        if (value is NotificationChannels channels)
        {
            if (channels == NotificationChannels.None) return this["Enum_NotificationChannels_None"];
            return string.Join(", ", Enum.GetValues<NotificationChannels>()
                .Where(channel => channel != NotificationChannels.None && channels.HasFlag(channel))
                .Select(channel => this[$"Enum_NotificationChannels_{channel}"]));
        }

        return this[$"Enum_{value.GetType().Name}_{value}"];
    }

    public string GetRuleName(AutomationRuleDefinition rule)
    {
        var key = $"RuleName_{rule.Id}";
        var value = this[key];
        return value == key ? rule.Name : value;
    }

    public string GetQuotaLabel(QuotaSnapshot snapshot) => snapshot.WindowKey.ToLowerInvariant() switch
    {
        "total" => this["Quota_Total"],
        "daily" => this["Quota_Daily"],
        "weekly" => this["Quota_Weekly"],
        "monthly" => this["Quota_Monthly"],
        "billing" => this["Quota_Billing"],
        "balance" => this["Quota_Balance"],
        "zhipu-token-5h" => this["Quota_ZhipuToken5h"],
        "zhipu-mcp-monthly" => this["Quota_ZhipuMcpMonthly"],
        _ => snapshot.Label
    };

    public void Apply(string language)
    {
        var normalized = string.Equals(language, "en-US", StringComparison.OrdinalIgnoreCase) ? "en-US" : DefaultLanguage;
        var culture = CultureInfo.GetCultureInfo(normalized);
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;

        var dictionaries = System.Windows.Application.Current.Resources.MergedDictionaries;
        _activeDictionary ??= dictionaries.FirstOrDefault(dictionary => dictionary.Source?.OriginalString.Contains("Strings.", StringComparison.Ordinal) == true);
        if (_activeDictionary is not null) dictionaries.Remove(_activeDictionary);
        _activeDictionary = new ResourceDictionary
        {
            Source = new Uri($"Resources/Strings.{normalized}.xaml", UriKind.Relative)
        };
        dictionaries.Add(_activeDictionary);
        CurrentLanguage = normalized;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }
}
