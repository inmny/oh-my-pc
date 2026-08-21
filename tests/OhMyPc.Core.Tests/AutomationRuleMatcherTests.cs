using System.Text.Json.Nodes;
using OhMyPc.Core.Domain;

namespace OhMyPc.Core.Tests;

public sealed class AutomationRuleMatcherTests
{
    private readonly AutomationRuleMatcher _matcher = new();

    [Fact]
    public void AllMode_RequiresEveryCondition()
    {
        var rule = Rule(
            AutomationMatchMode.All,
            Text("model", AutomationConditionOperator.Equal, "gpt-5.6-sol"),
            Boolean("available", true));
        var matching = Event("gpt-5.6-sol", true);
        var unavailable = Event("gpt-5.6-sol", false);

        Assert.True(_matcher.Matches(rule, matching));
        Assert.False(_matcher.Matches(rule, unavailable));
    }

    [Fact]
    public void AnyMode_MatchesOneCondition()
    {
        var rule = Rule(
            AutomationMatchMode.Any,
            Text("model", AutomationConditionOperator.Equal, "other-model"),
            Boolean("available", false));

        Assert.True(_matcher.Matches(rule, Event("gpt-5.6-sol", false)));
    }

    [Fact]
    public void TextContains_IsCaseInsensitive()
    {
        var rule = Rule(
            AutomationMatchMode.All,
            Text("model", AutomationConditionOperator.Contains, "GPT-5.6"));

        Assert.True(_matcher.Matches(rule, Event("gpt-5.6-sol", true)));
    }

    [Fact]
    public void MissingField_DoesNotMatch()
    {
        var rule = Rule(
            AutomationMatchMode.All,
            Number("latencyMs", AutomationConditionOperator.LessThan, 1000));

        Assert.False(_matcher.Matches(rule, Event("gpt-5.6-sol", true)));
    }

    [Fact]
    public void NumberCondition_MatchesIntegerEventValue()
    {
        var rule = Rule(
            AutomationMatchMode.All,
            Number("daysRemaining", AutomationConditionOperator.LessThanOrEqual, 7));
        var automationEvent = Event("gpt-5.6-sol", true);
        automationEvent.Fields["daysRemaining"] = 3;

        Assert.True(_matcher.Matches(rule, automationEvent));
    }

    private static AutomationRuleDefinition Rule(
        AutomationMatchMode matchMode,
        params AutomationConditionDefinition[] conditions) => new()
    {
        EventType = AutomationEventTypes.InputModelAvailabilityChanged,
        MatchMode = matchMode,
        Conditions = [.. conditions]
    };

    private static AutomationEvent Event(string model, bool available) => new()
    {
        Type = AutomationEventTypes.InputModelAvailabilityChanged,
        Fields = new JsonObject
        {
            ["model"] = model,
            ["available"] = available
        }
    };

    private static AutomationConditionDefinition Text(
        string field,
        AutomationConditionOperator @operator,
        string value) => new()
    {
        Field = field,
        Operator = @operator,
        ValueKind = AutomationValueKind.Text,
        Value = JsonValue.Create(value)
    };

    private static AutomationConditionDefinition Number(
        string field,
        AutomationConditionOperator @operator,
        double value) => new()
    {
        Field = field,
        Operator = @operator,
        ValueKind = AutomationValueKind.Number,
        Value = JsonValue.Create(value)
    };

    private static AutomationConditionDefinition Boolean(string field, bool value) => new()
    {
        Field = field,
        Operator = AutomationConditionOperator.Equal,
        ValueKind = AutomationValueKind.Boolean,
        Value = JsonValue.Create(value)
    };
}
