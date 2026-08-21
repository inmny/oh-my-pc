using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OhMyPc.Core;
using OhMyPc.Core.Domain;

namespace OhMyPc.Infrastructure.LocalUsage;

public sealed class TokscaleClient(
    LocalToolDetector detector,
    ILogger<TokscaleClient> logger) : ILocalUsageCollector
{
    public async Task<IReadOnlyList<UsageObservation>> CollectAsync(bool fullHistory, CancellationToken cancellationToken = default)
    {
        var clients = detector.DetectClients();
        if (clients.Count == 0) return [];

        var collectionDate = DateOnly.FromDateTime(DateTime.Now);
        var arguments = fullHistory
            ? new[] { "graph", "--client", string.Join(',', clients), "--no-spinner" }
            : new[] { "--json", "--client", string.Join(',', clients), "--group-by", "client,session,model", "--today" };
        var json = await RunAsync(arguments, fullHistory ? TimeSpan.FromMinutes(2) : TimeSpan.FromSeconds(60), cancellationToken);
        using var document = JsonDocument.Parse(json);
        return fullHistory ? ParseGraph(document.RootElement) : ParseToday(document.RootElement, collectionDate);
    }

    private async Task<string> RunAsync(IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var executable = ResolveExecutable();
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                logger.LogWarning(exception, "无法终止超时的 tokscale 进程");
            }

            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException($"tokscale 执行超过 {timeout.TotalSeconds:0} 秒。");
        }

        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(error)) logger.LogDebug("tokscale: {Message}", error.Trim());
        if (process.ExitCode != 0) throw new InvalidOperationException($"tokscale exited with code {process.ExitCode}: {error.Trim()}");
        return output;
    }

    private static string ResolveExecutable()
    {
        var executable = Path.Combine(AppContext.BaseDirectory, "tokscale", "tokscale.exe");
        if (!File.Exists(executable)) throw new FileNotFoundException("Bundled tokscale.exe was not found", executable);
        return executable;
    }

    internal static IReadOnlyList<UsageObservation> ParseToday(JsonElement root, DateOnly date)
    {
        var device = LocalUsageDevice.Id();
        var aggregate = new Dictionary<(string Client, string Provider, string Model), UsageObservation>();
        if (!root.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array) return [];

        foreach (var entry in entries.EnumerateArray())
        {
            var key = (
                Text(entry, "client", "unknown"),
                Text(entry, "provider", "unknown"),
                Text(entry, "model", "unknown"));
            if (!aggregate.TryGetValue(key, out var target))
            {
                target = new UsageObservation
                {
                    Date = date,
                    DeviceId = device,
                    Client = key.Item1,
                    Provider = key.Item2,
                    Model = key.Item3,
                    ObservedAt = DateTimeOffset.UtcNow
                };
                aggregate[key] = target;
            }

            target.InputTokens += Integer(entry, "input");
            target.OutputTokens += Integer(entry, "output");
            target.CacheReadTokens += Integer(entry, "cacheRead");
            target.CacheWriteTokens += Integer(entry, "cacheWrite");
            target.ReasoningTokens += Integer(entry, "reasoning");
            target.MessageCount += Integer(entry, "messageCount");
            target.CostUsd += Decimal(entry, "cost");
        }

        return aggregate.Values.ToList();
    }

    internal static IReadOnlyList<UsageObservation> ParseGraph(JsonElement root)
    {
        var output = new List<UsageObservation>();
        var device = LocalUsageDevice.Id();
        if (!root.TryGetProperty("contributions", out var contributions) || contributions.ValueKind != JsonValueKind.Array) return output;

        foreach (var contribution in contributions.EnumerateArray())
        {
            if (!DateOnly.TryParseExact(Text(contribution, "date", ""), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) continue;
            var observedAt = DateTimeOffset.UtcNow;
            if (contribution.TryGetProperty("clients", out var clients) && clients.ValueKind == JsonValueKind.Array)
            {
                foreach (var client in clients.EnumerateArray())
                {
                    var tokens = client.TryGetProperty("tokens", out var tokenValue) ? tokenValue : default;
                    output.Add(new UsageObservation
                    {
                        Date = date,
                        DeviceId = device,
                        Client = Text(client, "client", "unknown"),
                        Provider = Text(client, "providerId", "unknown"),
                        Model = Text(client, "modelId", "unknown"),
                        InputTokens = Integer(tokens, "input"),
                        OutputTokens = Integer(tokens, "output"),
                        CacheReadTokens = Integer(tokens, "cacheRead"),
                        CacheWriteTokens = Integer(tokens, "cacheWrite"),
                        ReasoningTokens = Integer(tokens, "reasoning"),
                        MessageCount = Integer(client, "messages"),
                        CostUsd = Decimal(client, "cost"),
                        ObservedAt = observedAt
                    });
                }
            }

            var activeTime = Integer(contribution, "activeTimeMs");
            if (activeTime > 0)
            {
                output.Add(new UsageObservation
                {
                    Date = date,
                    DeviceId = device,
                    Client = "_activity",
                    Provider = "tokscale",
                    Model = "active-time",
                    ActiveTimeMs = activeTime,
                    ObservedAt = observedAt
                });
            }
        }

        return output;
    }

    private static string Text(JsonElement value, string property, string fallback)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(property, out var item)) return fallback;
        return item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()) ? item.GetString()! : fallback;
    }

    private static long Integer(JsonElement value, string property)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(property, out var item)) return 0;
        if (item.TryGetInt64(out var number)) return number;
        return item.TryGetDouble(out var floating) ? (long)Math.Round(floating) : 0;
    }

    private static decimal Decimal(JsonElement value, string property)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(property, out var item)) return 0m;
        if (item.TryGetDecimal(out var number)) return number;
        return item.TryGetDouble(out var floating) ? (decimal)floating : 0m;
    }
}
