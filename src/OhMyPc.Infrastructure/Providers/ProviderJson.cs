using System.Text.Json;

namespace OhMyPc.Infrastructure.Providers;

internal static class ProviderJson
{
    public static double Number(JsonElement value, string property, double fallback = 0)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(property, out var item)) return fallback;
        if (item.TryGetDouble(out var number)) return number;
        return item.ValueKind == JsonValueKind.String && double.TryParse(item.GetString(), out number) ? number : fallback;
    }

    public static string Text(JsonElement value, string property, string fallback = "")
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(property, out var item) || item.ValueKind != JsonValueKind.String) return fallback;
        return item.GetString() ?? fallback;
    }

    public static DateTimeOffset? Date(JsonElement value, string property)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(property, out var item)) return null;
        if (item.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(item.GetString(), out var date)) return date;
        if (item.TryGetInt64(out var unix) && unix > 0) return DateTimeOffset.FromUnixTimeSeconds(unix);
        return null;
    }
}
