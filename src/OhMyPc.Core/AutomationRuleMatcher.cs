using System.Text.Json;
using System.Text.Json.Nodes;
using OhMyPc.Core.Domain;

namespace OhMyPc.Core;

public sealed class AutomationRuleMatcher
{
    public bool Matches(AutomationRuleDefinition rule, AutomationEvent automationEvent)
    {
        if (!string.Equals(rule.EventType, automationEvent.Type, StringComparison.Ordinal)) return false;
        if (rule.Conditions.Count == 0) return true;

        return rule.MatchMode == AutomationMatchMode.All
            ? rule.Conditions.All(condition => Matches(condition, automationEvent.Fields))
            : rule.Conditions.Any(condition => Matches(condition, automationEvent.Fields));
    }

    private static bool Matches(AutomationConditionDefinition condition, JsonObject fields)
    {
        if (!fields.TryGetPropertyValue(condition.Field, out var actual)
            || actual is null
            || condition.Value is null)
        {
            return false;
        }

        return condition.ValueKind switch
        {
            AutomationValueKind.Text => CompareText(
                actual.GetValue<string>(),
                condition.Value.GetValue<string>(),
                condition.Operator),
            AutomationValueKind.Number => TryGetNumber(actual, out var actualNumber)
                && TryGetNumber(condition.Value, out var expectedNumber)
                && CompareNumber(actualNumber, expectedNumber, condition.Operator),
            AutomationValueKind.Boolean => CompareBoolean(
                actual.GetValue<bool>(),
                condition.Value.GetValue<bool>(),
                condition.Operator),
            _ => false
        };
    }

    private static bool CompareText(string actual, string expected, AutomationConditionOperator @operator) => @operator switch
    {
        AutomationConditionOperator.Equal => string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase),
        AutomationConditionOperator.NotEqual => !string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase),
        AutomationConditionOperator.Contains => actual.Contains(expected, StringComparison.OrdinalIgnoreCase),
        _ => false
    };

    private static bool CompareNumber(double actual, double expected, AutomationConditionOperator @operator) => @operator switch
    {
        AutomationConditionOperator.LessThanOrEqual => actual <= expected,
        AutomationConditionOperator.GreaterThanOrEqual => actual >= expected,
        AutomationConditionOperator.Equal => Math.Abs(actual - expected) < 0.000001,
        AutomationConditionOperator.NotEqual => Math.Abs(actual - expected) >= 0.000001,
        AutomationConditionOperator.LessThan => actual < expected,
        AutomationConditionOperator.GreaterThan => actual > expected,
        _ => false
    };

    private static bool TryGetNumber(JsonNode node, out double number)
    {
        var value = node.AsValue();
        if (value.TryGetValue<JsonElement>(out var element) && element.ValueKind == JsonValueKind.Number)
        {
            number = element.GetDouble();
            return true;
        }

        if (value.TryGetValue<double>(out number)) return true;
        if (value.TryGetValue<float>(out var single))
        {
            number = single;
            return true;
        }
        if (value.TryGetValue<decimal>(out var decimalValue))
        {
            number = (double)decimalValue;
            return true;
        }
        if (value.TryGetValue<long>(out var longValue))
        {
            number = longValue;
            return true;
        }
        if (value.TryGetValue<ulong>(out var unsignedLongValue))
        {
            number = unsignedLongValue;
            return true;
        }
        if (value.TryGetValue<int>(out var integerValue))
        {
            number = integerValue;
            return true;
        }
        if (value.TryGetValue<uint>(out var unsignedIntegerValue))
        {
            number = unsignedIntegerValue;
            return true;
        }

        number = 0;
        return false;
    }

    private static bool CompareBoolean(bool actual, bool expected, AutomationConditionOperator @operator) => @operator switch
    {
        AutomationConditionOperator.Equal => actual == expected,
        AutomationConditionOperator.NotEqual => actual != expected,
        _ => false
    };
}
