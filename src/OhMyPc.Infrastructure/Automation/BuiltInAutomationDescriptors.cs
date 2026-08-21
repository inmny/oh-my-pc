using OhMyPc.Core;
using OhMyPc.Core.Domain;
using OhMyPc.Infrastructure.InputStatus;

namespace OhMyPc.Infrastructure.Automation;

public sealed class UsageAutomationDescriptorProvider : IAutomationEventDescriptorProvider
{
    private static readonly AutomationConditionOperator[] TextOperators =
    [
        AutomationConditionOperator.Equal,
        AutomationConditionOperator.NotEqual,
        AutomationConditionOperator.Contains
    ];

    private static readonly AutomationConditionOperator[] ChoiceOperators =
    [
        AutomationConditionOperator.Equal,
        AutomationConditionOperator.NotEqual
    ];

    private static readonly AutomationConditionOperator[] NumberOperators =
    [
        AutomationConditionOperator.LessThanOrEqual,
        AutomationConditionOperator.GreaterThanOrEqual,
        AutomationConditionOperator.LessThan,
        AutomationConditionOperator.GreaterThan,
        AutomationConditionOperator.Equal,
        AutomationConditionOperator.NotEqual
    ];

    public IReadOnlyList<AutomationEventDescriptor> Descriptors { get; } =
    [
        new AutomationEventDescriptor
        {
            EventType = AutomationEventTypes.QuotaObserved,
            DisplayNameKey = "AutomationEvent_QuotaObserved",
            Fields =
            [
                Text("sourceId", "AutomationField_Source", AutomationOptionProviderKeys.DataSources, ChoiceOperators),
                Text("windowKey", "AutomationField_QuotaWindow", AutomationOptionProviderKeys.QuotaWindows, ChoiceOperators),
                Number("remainingPercent", "AutomationField_RemainingPercent"),
                Number("remaining", "AutomationField_Remaining"),
                Number("used", "AutomationField_Used"),
                Text("unit", "AutomationField_Unit", operators: TextOperators)
            ]
        },
        new AutomationEventDescriptor
        {
            EventType = AutomationEventTypes.ProviderStatusChanged,
            DisplayNameKey = "AutomationEvent_ProviderStatusChanged",
            Fields =
            [
                Text("sourceId", "AutomationField_Source", AutomationOptionProviderKeys.DataSources, ChoiceOperators),
                Text("previousStatus", "AutomationField_PreviousStatus", operators: TextOperators),
                Text("currentStatus", "AutomationField_CurrentStatus", operators: TextOperators)
            ]
        },
        new AutomationEventDescriptor
        {
            EventType = AutomationEventTypes.DailyUsageUpdated,
            DisplayNameKey = "AutomationEvent_DailyUsageUpdated",
            Fields =
            [
                Number("totalTokens", "AutomationField_TotalTokens"),
                Text("date", "AutomationField_Date", operators: TextOperators)
            ]
        },
        new AutomationEventDescriptor
        {
            EventType = AutomationEventTypes.VpnQuotaObserved,
            DisplayNameKey = "AutomationEvent_VpnQuotaObserved",
            Fields =
            [
                Text("email", "AutomationField_Email", operators: TextOperators),
                Text("planName", "AutomationField_PlanName", operators: TextOperators),
                Number("remainingPercent", "AutomationField_RemainingPercent"),
                Number("remainingGiB", "AutomationField_RemainingGib"),
                Number("usedGiB", "AutomationField_UsedGib"),
                Number("limitGiB", "AutomationField_LimitGib")
            ]
        },
        new AutomationEventDescriptor
        {
            EventType = AutomationEventTypes.VpnExpirationObserved,
            DisplayNameKey = "AutomationEvent_VpnExpirationObserved",
            Fields =
            [
                Text("email", "AutomationField_Email", operators: TextOperators),
                Text("planName", "AutomationField_PlanName", operators: TextOperators),
                Number("daysRemaining", "AutomationField_DaysRemaining"),
                Text("expiresAt", "AutomationField_ExpiresAt", operators: TextOperators),
                Boolean("expired", "AutomationField_Expired")
            ]
        },
        new AutomationEventDescriptor
        {
            EventType = AutomationEventTypes.VpnStatusChanged,
            DisplayNameKey = "AutomationEvent_VpnStatusChanged",
            Fields =
            [
                Text("email", "AutomationField_Email", operators: TextOperators),
                Text("previousStatus", "AutomationField_PreviousStatus", operators: TextOperators),
                Text("currentStatus", "AutomationField_CurrentStatus", operators: TextOperators),
                Boolean("authenticationFailed", "AutomationField_AuthenticationFailed"),
                Text("error", "AutomationField_Error", operators: TextOperators)
            ]
        }
    ];

    private static AutomationFieldDescriptor Text(
        string key,
        string displayNameKey,
        string? optionProviderKey = null,
        IReadOnlyList<AutomationConditionOperator>? operators = null) => new()
    {
        Key = key,
        DisplayNameKey = displayNameKey,
        ValueKind = AutomationValueKind.Text,
        Operators = operators ?? TextOperators,
        OptionProviderKey = optionProviderKey
    };

    private static AutomationFieldDescriptor Number(string key, string displayNameKey) => new()
    {
        Key = key,
        DisplayNameKey = displayNameKey,
        ValueKind = AutomationValueKind.Number,
        Operators = NumberOperators
    };

    private static AutomationFieldDescriptor Boolean(string key, string displayNameKey) => new()
    {
        Key = key,
        DisplayNameKey = displayNameKey,
        ValueKind = AutomationValueKind.Boolean,
        Operators = ChoiceOperators
    };
}

public sealed class InputStatusAutomationDescriptorProvider : IAutomationEventDescriptorProvider
{
    public IReadOnlyList<AutomationEventDescriptor> Descriptors { get; } =
    [
        new AutomationEventDescriptor
        {
            EventType = AutomationEventTypes.InputModelAvailabilityChanged,
            DisplayNameKey = "AutomationEvent_InputModelAvailabilityChanged",
            Fields =
            [
                new AutomationFieldDescriptor
                {
                    Key = "sourceId",
                    DisplayNameKey = "AutomationField_Source",
                    ValueKind = AutomationValueKind.Text,
                    Operators = [AutomationConditionOperator.Equal, AutomationConditionOperator.NotEqual],
                    OptionProviderKey = AutomationOptionProviderKeys.DataSources
                },
                new AutomationFieldDescriptor
                {
                    Key = "model",
                    DisplayNameKey = "AutomationField_Model",
                    ValueKind = AutomationValueKind.Text,
                    Operators = [AutomationConditionOperator.Equal, AutomationConditionOperator.NotEqual],
                    OptionProviderKey = AutomationOptionProviderKeys.InputModels
                },
                new AutomationFieldDescriptor
                {
                    Key = "available",
                    DisplayNameKey = "AutomationField_Available",
                    ValueKind = AutomationValueKind.Boolean,
                    Operators = [AutomationConditionOperator.Equal, AutomationConditionOperator.NotEqual]
                },
                new AutomationFieldDescriptor
                {
                    Key = "previousAvailable",
                    DisplayNameKey = "AutomationField_PreviousAvailable",
                    ValueKind = AutomationValueKind.Boolean,
                    Operators = [AutomationConditionOperator.Equal, AutomationConditionOperator.NotEqual]
                },
                new AutomationFieldDescriptor
                {
                    Key = "latencyMs",
                    DisplayNameKey = "AutomationField_Latency",
                    ValueKind = AutomationValueKind.Number,
                    Operators =
                    [
                        AutomationConditionOperator.LessThanOrEqual,
                        AutomationConditionOperator.GreaterThanOrEqual,
                        AutomationConditionOperator.LessThan,
                        AutomationConditionOperator.GreaterThan,
                        AutomationConditionOperator.Equal,
                        AutomationConditionOperator.NotEqual
                    ]
                },
                new AutomationFieldDescriptor
                {
                    Key = "error",
                    DisplayNameKey = "AutomationField_Error",
                    ValueKind = AutomationValueKind.Text,
                    Operators =
                    [
                        AutomationConditionOperator.Equal,
                        AutomationConditionOperator.NotEqual,
                        AutomationConditionOperator.Contains
                    ]
                }
            ]
        }
    ];
}

public sealed class DataSourceAutomationOptionsProvider(IAppStore store) : IAutomationValueOptionsProvider
{
    public string Key => AutomationOptionProviderKeys.DataSources;

    public async Task<IReadOnlyList<AutomationValueOption>> GetOptionsAsync(CancellationToken cancellationToken = default) =>
        (await store.ListDataSourcesAsync(cancellationToken))
        .Select(source => new AutomationValueOption { Value = source.Id, DisplayName = source.Name })
        .ToArray();
}

public sealed class QuotaWindowAutomationOptionsProvider(IAppStore store) : IAutomationValueOptionsProvider
{
    public string Key => AutomationOptionProviderKeys.QuotaWindows;

    public async Task<IReadOnlyList<AutomationValueOption>> GetOptionsAsync(CancellationToken cancellationToken = default) =>
        (await store.ListCurrentQuotasAsync(cancellationToken))
        .GroupBy(quota => quota.WindowKey)
        .Select(group => new AutomationValueOption { Value = group.Key, DisplayName = group.First().Label })
        .OrderBy(option => option.DisplayName)
        .ToArray();
}

public sealed class InputModelAutomationOptionsProvider(InputStatusRefreshService status) : IAutomationValueOptionsProvider
{
    public string Key => AutomationOptionProviderKeys.InputModels;

    public Task<IReadOnlyList<AutomationValueOption>> GetOptionsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<AutomationValueOption>>(status.ListSnapshots()
            .SelectMany(snapshot => snapshot.Models)
            .Select(model => model.Model)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(model => model)
            .Select(model => new AutomationValueOption { Value = model, DisplayName = model })
            .ToArray());
}
