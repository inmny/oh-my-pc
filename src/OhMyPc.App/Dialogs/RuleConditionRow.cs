using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using OhMyPc.App.Services;
using OhMyPc.Core;
using OhMyPc.Core.Domain;

namespace OhMyPc.App.Dialogs;

public sealed class RuleConditionRow : INotifyPropertyChanged
{
    private readonly IAutomationCatalog _catalog;
    private readonly LocalizationService _text;
    private RuleFieldChoice _selectedField;
    private AutomationConditionOperator _selectedOperator;
    private string _valueText = "";
    private IReadOnlyList<AutomationValueOption> _options = [];

    public RuleConditionRow(
        IAutomationCatalog catalog,
        LocalizationService text,
        IReadOnlyList<RuleFieldChoice> fields,
        AutomationConditionDefinition? condition = null)
    {
        _catalog = catalog;
        _text = text;
        Fields = fields;
        _selectedField = condition is null
            ? fields[0]
            : fields.Single(field => field.Descriptor.Key == condition.Field);
        Operators = _selectedField.Descriptor.Operators;
        _selectedOperator = condition?.Operator ?? Operators[0];
        _valueText = condition?.Value is null ? "" : FormatValue(condition);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public IReadOnlyList<RuleFieldChoice> Fields { get; }
    public IReadOnlyList<AutomationConditionOperator> Operators { get; private set; }
    public IReadOnlyList<AutomationValueOption> Options { get => _options; private set => Set(ref _options, value); }

    public RuleFieldChoice SelectedField
    {
        get => _selectedField;
        set
        {
            if (_selectedField == value) return;
            _selectedField = value;
            Operators = value.Descriptor.Operators;
            _selectedOperator = Operators[0];
            _valueText = "";
            Options = [];
            Raise();
            Raise(nameof(Operators));
            Raise(nameof(SelectedOperator));
            Raise(nameof(ValueText));
            Raise(nameof(UsesOptionPicker));
            Raise(nameof(UsesTextInput));
        }
    }

    public AutomationConditionOperator SelectedOperator
    {
        get => _selectedOperator;
        set => Set(ref _selectedOperator, value);
    }

    public string ValueText
    {
        get => _valueText;
        set => Set(ref _valueText, value);
    }

    public bool UsesOptionPicker =>
        SelectedField.Descriptor.ValueKind == AutomationValueKind.Boolean
        || SelectedField.Descriptor.OptionProviderKey is not null;

    public bool UsesTextInput => !UsesOptionPicker;

    public async Task LoadOptionsAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<AutomationValueOption> options;
        if (SelectedField.Descriptor.ValueKind == AutomationValueKind.Boolean)
        {
            options =
            [
                new AutomationValueOption { Value = "true", DisplayName = _text["Common_Yes"] },
                new AutomationValueOption { Value = "false", DisplayName = _text["Common_No"] }
            ];
        }
        else if (SelectedField.Descriptor.OptionProviderKey is not null)
        {
            options = await _catalog.GetOptionsAsync(SelectedField.Descriptor.OptionProviderKey, cancellationToken);
        }
        else
        {
            options = [];
        }

        if (!string.IsNullOrWhiteSpace(ValueText)
            && options.All(option => !string.Equals(option.Value, ValueText, StringComparison.OrdinalIgnoreCase)))
        {
            options = options.Append(new AutomationValueOption { Value = ValueText, DisplayName = ValueText }).ToArray();
        }
        Options = options;
    }

    public bool TryBuild(out AutomationConditionDefinition condition)
    {
        JsonNode? value = SelectedField.Descriptor.ValueKind switch
        {
            AutomationValueKind.Text when !string.IsNullOrWhiteSpace(ValueText) => JsonValue.Create(ValueText.Trim()),
            AutomationValueKind.Number when double.TryParse(ValueText, NumberStyles.Float, CultureInfo.CurrentCulture, out var number) => JsonValue.Create(number),
            AutomationValueKind.Boolean when bool.TryParse(ValueText, out var boolean) => JsonValue.Create(boolean),
            _ => null
        };

        condition = new AutomationConditionDefinition
        {
            Field = SelectedField.Descriptor.Key,
            Operator = SelectedOperator,
            ValueKind = SelectedField.Descriptor.ValueKind,
            Value = value
        };
        return value is not null;
    }

    private static string FormatValue(AutomationConditionDefinition condition) => condition.ValueKind switch
    {
        AutomationValueKind.Text => condition.Value!.GetValue<string>(),
        AutomationValueKind.Number => condition.Value!.GetValue<double>().ToString("0.######", CultureInfo.CurrentCulture),
        AutomationValueKind.Boolean => condition.Value!.GetValue<bool>().ToString().ToLowerInvariant(),
        _ => ""
    };

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(propertyName);
        return true;
    }

    private void Raise([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed record RuleFieldChoice(AutomationFieldDescriptor Descriptor, string DisplayName);
public sealed record RuleEventChoice(AutomationEventDescriptor Descriptor, string DisplayName);
