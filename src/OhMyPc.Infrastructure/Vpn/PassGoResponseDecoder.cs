using System.Text;
using System.Text.Json;

namespace OhMyPc.Infrastructure.Vpn;

public static class PassGoResponseDecoder
{
    private const string Source = "nsz{gAWrkXlx08J6Eq:V4[deO1DQTCwm2oB3ty9jSYI]7RM5bHiUaf,c}KuPGpNhZLvF";
    private const string Target = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789,[]{}:";

    public static JsonDocument Decode(string payload)
    {
        var value = Encoding.Latin1.GetString(Convert.FromBase64String(payload));
        for (var round = 0; round < 10; round++)
        {
            value = string.Create(value.Length, value, static (characters, source) =>
            {
                for (var index = 0; index < source.Length; index++)
                {
                    var mappedIndex = Source.IndexOf(source[index]);
                    characters[index] = mappedIndex >= 0 ? Target[mappedIndex] : source[index];
                }
            });
        }

        return JsonDocument.Parse(value);
    }
}
