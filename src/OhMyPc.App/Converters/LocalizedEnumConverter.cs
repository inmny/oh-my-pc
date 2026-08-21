using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Microsoft.Extensions.DependencyInjection;
using OhMyPc.App.Services;
using OhMyPc.Core;
using OhMyPc.Core.Domain;

namespace OhMyPc.App.Converters;

public sealed class LocalizedEnumConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not Enum enumValue) return value?.ToString() ?? "";
        var localizer = ((App)System.Windows.Application.Current).Services.GetRequiredService<LocalizationService>();
        return localizer.GetEnum(enumValue);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => System.Windows.Data.Binding.DoNothing;
}

public sealed class LocalizedRuleNameConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not AutomationRuleDefinition rule) return "";
        var key = $"RuleName_{rule.Id}";
        var localized = System.Windows.Application.Current.TryFindResource(key) as string;
        return localized ?? rule.Name;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => System.Windows.Data.Binding.DoNothing;
}

public sealed class LocalizedAutomationEventConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string eventType) return "";
        var services = ((App)System.Windows.Application.Current).Services;
        var catalog = services.GetRequiredService<IAutomationCatalog>();
        var text = services.GetRequiredService<LocalizationService>();
        return text[catalog.Events.Single(descriptor => descriptor.EventType == eventType).DisplayNameKey];
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => System.Windows.Data.Binding.DoNothing;
}

public sealed class AutomationConditionsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not AutomationRuleDefinition rule) return "";
        var services = ((App)System.Windows.Application.Current).Services;
        var catalog = services.GetRequiredService<IAutomationCatalog>();
        var text = services.GetRequiredService<LocalizationService>();
        if (rule.Conditions.Count == 0) return text["Rules_NoConditions"];

        var descriptor = catalog.Events.Single(item => item.EventType == rule.EventType);
        return string.Join("; ", rule.Conditions.Select(condition =>
        {
            var field = descriptor.Fields.Single(item => item.Key == condition.Field);
            var valueText = condition.ValueKind switch
            {
                AutomationValueKind.Text => condition.Value!.GetValue<string>(),
                AutomationValueKind.Number => condition.Value!.GetValue<double>().ToString("0.##", culture),
                AutomationValueKind.Boolean => condition.Value!.GetValue<bool>() ? text["Common_Yes"] : text["Common_No"],
                _ => ""
            };
            return $"{text[field.DisplayNameKey]} {text.GetEnum(condition.Operator)} {valueText}";
        }));
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => System.Windows.Data.Binding.DoNothing;
}

public sealed class AutomationChannelsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not AutomationRuleDefinition rule) return "";
        var action = rule.Actions.Single(item => item.Kind == AutomationActionKinds.LocalNotification);
        var options = LocalNotificationActionOptions.FromDefinition(action);
        var text = ((App)System.Windows.Application.Current).Services.GetRequiredService<LocalizationService>();
        return text.GetEnum(options.Channels);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => System.Windows.Data.Binding.DoNothing;
}
