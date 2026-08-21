using System.Collections.ObjectModel;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using OhMyPc.App.Services;
using OhMyPc.Core;
using OhMyPc.Core.Domain;

namespace OhMyPc.App.Dialogs;

public partial class RuleDialog : Window
{
    private readonly string _id;
    private readonly IAutomationCatalog _catalog;
    private readonly LocalizationService _text;
    private readonly ObservableCollection<RuleConditionRow> _conditions = [];
    private bool _initializing = true;

    public RuleDialog(
        LocalizationService text,
        IAutomationCatalog catalog,
        AutomationRuleDefinition? rule)
    {
        _text = text;
        _catalog = catalog;
        InitializeComponent();
        ConditionsList.ItemsSource = _conditions;
        EventBox.ItemsSource = catalog.Events
            .Select(descriptor => new RuleEventChoice(descriptor, text[descriptor.DisplayNameKey]))
            .ToArray();
        SeverityBox.ItemsSource = Enum.GetValues<NotificationSeverity>();

        rule ??= NewRule(text);
        _id = rule.Id;
        NameBox.Text = text.GetRuleName(rule);
        EventBox.SelectedItem = ((IEnumerable<RuleEventChoice>)EventBox.ItemsSource)
            .Single(choice => choice.Descriptor.EventType == rule.EventType);
        MatchAllBox.IsChecked = rule.MatchMode == AutomationMatchMode.All;
        MatchAnyBox.IsChecked = rule.MatchMode == AutomationMatchMode.Any;

        var fields = FieldsForSelectedEvent();
        foreach (var condition in rule.Conditions)
        {
            _conditions.Add(new RuleConditionRow(catalog, text, fields, condition));
        }

        var notification = LocalNotificationActionOptions.FromDefinition(
            rule.Actions.Single(action => action.Kind == AutomationActionKinds.LocalNotification));
        SeverityBox.SelectedItem = notification.Severity;
        DanmakuBox.IsChecked = notification.Channels.HasFlag(NotificationChannels.Danmaku);
        SystemBox.IsChecked = notification.Channels.HasFlag(NotificationChannels.System);
        TrayBox.IsChecked = notification.Channels.HasFlag(NotificationChannels.Tray);
        CooldownBox.Text = rule.CooldownMinutes.ToString();
        QuietHoursBox.IsChecked = rule.RespectQuietHours;
        EnabledBox.IsChecked = rule.Enabled;
        _initializing = false;
    }

    public AutomationRuleDefinition Rule { get; private set; } = null!;

    private async void Window_Loaded(object sender, RoutedEventArgs e) => await LoadOptionsAsync(_conditions);

    private async void EventBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || EventBox.SelectedItem is null) return;
        _conditions.Clear();
        var row = new RuleConditionRow(_catalog, _text, FieldsForSelectedEvent());
        _conditions.Add(row);
        await LoadOptionsAsync([row]);
    }

    private async void ConditionField_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not RuleConditionRow row) return;
        await LoadOptionsAsync([row]);
    }

    private async void AddCondition_Click(object sender, RoutedEventArgs e)
    {
        var row = new RuleConditionRow(_catalog, _text, FieldsForSelectedEvent());
        _conditions.Add(row);
        await LoadOptionsAsync([row]);
    }

    private void RemoveCondition_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is RuleConditionRow row) _conditions.Remove(row);
    }

    private async Task LoadOptionsAsync(IEnumerable<RuleConditionRow> rows)
    {
        OptionsStatusText.Text = "";
        try
        {
            foreach (var row in rows.Where(row => row.UsesOptionPicker))
            {
                await row.LoadOptionsAsync();
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            OptionsStatusText.Text = _text["RuleDialog_OptionsLoadFailed"];
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text)
            || !int.TryParse(CooldownBox.Text, out var cooldown)
            || cooldown < 0)
        {
            ShowInvalidRule();
            return;
        }

        var conditions = new List<AutomationConditionDefinition>();
        foreach (var row in _conditions)
        {
            if (!row.TryBuild(out var condition))
            {
                ShowInvalidRule();
                return;
            }
            conditions.Add(condition);
        }

        var channels = NotificationChannels.None;
        if (DanmakuBox.IsChecked == true) channels |= NotificationChannels.Danmaku;
        if (SystemBox.IsChecked == true) channels |= NotificationChannels.System;
        if (TrayBox.IsChecked == true) channels |= NotificationChannels.Tray;
        if (channels == NotificationChannels.None)
        {
            System.Windows.MessageBox.Show(
                _text["Message_SelectChannel"],
                _text["RuleDialog_Title"],
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        Rule = new AutomationRuleDefinition
        {
            Id = _id,
            Name = NameBox.Text.Trim(),
            Enabled = EnabledBox.IsChecked == true,
            EventType = ((RuleEventChoice)EventBox.SelectedItem).Descriptor.EventType,
            MatchMode = MatchAnyBox.IsChecked == true ? AutomationMatchMode.Any : AutomationMatchMode.All,
            Conditions = conditions,
            Actions =
            [
                new LocalNotificationActionOptions
                {
                    Channels = channels,
                    Severity = (NotificationSeverity)SeverityBox.SelectedItem
                }.ToDefinition()
            ],
            CooldownMinutes = cooldown,
            RespectQuietHours = QuietHoursBox.IsChecked == true
        };
        DialogResult = true;
    }

    private IReadOnlyList<RuleFieldChoice> FieldsForSelectedEvent() =>
        ((RuleEventChoice)EventBox.SelectedItem).Descriptor.Fields
        .Select(field => new RuleFieldChoice(field, _text[field.DisplayNameKey]))
        .ToArray();

    private void ShowInvalidRule() => System.Windows.MessageBox.Show(
        _text["Message_InvalidRule"],
        _text["RuleDialog_Title"],
        MessageBoxButton.OK,
        MessageBoxImage.Warning);

    private static AutomationRuleDefinition NewRule(LocalizationService text) => new()
    {
        Name = text["RuleDialog_NewRule"],
        EventType = AutomationEventTypes.QuotaObserved,
        Conditions =
        [
            new AutomationConditionDefinition
            {
                Field = "remainingPercent",
                Operator = AutomationConditionOperator.LessThanOrEqual,
                ValueKind = AutomationValueKind.Number,
                Value = JsonValue.Create(20d)
            }
        ],
        Actions =
        [
            new LocalNotificationActionOptions
            {
                Channels = NotificationChannels.Danmaku | NotificationChannels.Tray,
                Severity = NotificationSeverity.Warning
            }.ToDefinition()
        ],
        CooldownMinutes = 240,
        RespectQuietHours = true
    };
}
