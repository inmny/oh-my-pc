namespace OhMyPc.Infrastructure.LocalUsage;

internal static class LocalUsageDevice
{
    public static string Id()
    {
        var value = Environment.MachineName.ToLowerInvariant();
        return new string(value.Select(character => char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '-').ToArray());
    }
}
