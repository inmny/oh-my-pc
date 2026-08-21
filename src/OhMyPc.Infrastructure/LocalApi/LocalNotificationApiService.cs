using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OhMyPc.Core;
using OhMyPc.Core.Domain;

namespace OhMyPc.Infrastructure.LocalApi;

public sealed class LocalNotificationApiException(string message, Exception innerException)
    : Exception(message, innerException);

public sealed class DanmakuNotificationRequest
{
    public string? Source { get; init; }
    public string? Title { get; init; }
    public string? Body { get; init; }
    public NotificationSeverity Severity { get; init; } = NotificationSeverity.Info;
}

public sealed class LocalNotificationApiService(
    INotificationSink notificationSink,
    ILogger<LocalNotificationApiService> logger) : IHostedService, IDisposable
{
    public const int MinimumPort = 1024;
    public const int MaximumPort = 65535;
    public const string DanmakuPath = "/api/v1/danmaku";
    public const string HealthPath = "/health";

    private const int MaximumSourceLength = 80;
    private const int MaximumTitleLength = 120;
    private const int MaximumBodyLength = 1000;
    private const int RequestsPerMinute = 60;
    private const string RateLimitPolicy = "danmaku";
    private const long MaximumRequestBodyBytes = 16 * 1024;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private WebApplication? _application;
    private int? _activePort;
    private bool _disposed;

    public bool IsRunning => _application is not null;
    public int? ActivePort => _activePort;
    public string? LastError { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task ApplySettingsAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var enabled = settings.LocalApiEnabled;
        var port = settings.LocalApiPort;
        if (enabled && !IsValidPort(port))
        {
            throw new ArgumentOutOfRangeException(
                nameof(settings.LocalApiPort),
                port,
                $"端口必须介于 {MinimumPort} 和 {MaximumPort} 之间。");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!enabled)
            {
                LastError = null;
                await StopActiveApplicationAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            if (_application is not null && _activePort == port)
            {
                LastError = null;
                return;
            }

            var replacement = await StartApplicationAsync(port, cancellationToken).ConfigureAwait(false);
            var previous = _application;
            _application = replacement;
            _activePort = port;
            LastError = null;

            if (previous is not null)
            {
                try
                {
                    await StopApplicationAsync(previous, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "旧的本地弹幕 API 监听器停止失败");
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_disposed) return;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopActiveApplicationAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public static bool IsValidPort(int port) => port is >= MinimumPort and <= MaximumPort;

    private async Task<WebApplication> StartApplicationAsync(int port, CancellationToken cancellationToken)
    {
        WebApplication? application = null;
        try
        {
            application = BuildApplication(port);
            await application.StartAsync(cancellationToken).ConfigureAwait(false);
            logger.LogInformation("本地弹幕 API 已在端口 {Port} 启动", port);
            return application;
        }
        catch (OperationCanceledException)
        {
            if (application is not null) await DisposeQuietlyAsync(application).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception)
        {
            if (application is not null) await DisposeQuietlyAsync(application).ConfigureAwait(false);
            var failure = new LocalNotificationApiException($"无法在本机端口 {port} 启动弹幕 API。", exception);
            LastError = failure.Message;
            throw failure;
        }
    }

    private WebApplication BuildApplication(int port)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = [],
            ApplicationName = typeof(LocalNotificationApiService).Assembly.GetName().Name,
            EnvironmentName = Environments.Production
        });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection();
        builder.WebHost.UseSetting(WebHostDefaults.PreventHostingStartupKey, "true");
        builder.WebHost.UseSetting(WebHostDefaults.ServerUrlsKey, string.Empty);
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Limits.MaxRequestBodySize = MaximumRequestBodyBytes;
            options.ListenLocalhost(port);
        });
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.Converters.Add(
                new JsonStringEnumConverter<NotificationSeverity>(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        });
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddFixedWindowLimiter(RateLimitPolicy, limiter =>
            {
                limiter.PermitLimit = RequestsPerMinute;
                limiter.Window = TimeSpan.FromMinutes(1);
                limiter.QueueLimit = 0;
                limiter.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
                limiter.AutoReplenishment = true;
            });
        });

        var application = builder.Build();
        application.UseRateLimiter();
        application.MapGet(HealthPath, () => Results.Ok(new { status = "ok" }));
        application.MapPost(DanmakuPath, PublishDanmakuAsync).RequireRateLimiting(RateLimitPolicy);
        return application;
    }

    private async Task<IResult> PublishDanmakuAsync(
        DanmakuNotificationRequest request,
        CancellationToken cancellationToken)
    {
        var errors = Validate(request);
        if (errors.Count > 0) return Results.ValidationProblem(errors);

        var source = string.IsNullOrWhiteSpace(request.Source) ? "local-api" : request.Source.Trim();
        var title = string.IsNullOrWhiteSpace(request.Title) ? "Oh My PC" : request.Title.Trim();
        try
        {
            var record = await notificationSink.PublishAsync(new NotificationMessage
            {
                Origin = NotificationOrigin.LocalApi,
                Source = source,
                Title = title,
                Body = request.Body!.Trim(),
                Channels = NotificationChannels.Danmaku,
                Severity = request.Severity,
                SubjectKey = "local-api"
            }, cancellationToken);
            return Results.Accepted(value: new { id = record.Id, createdAt = record.CreatedAt });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "本地弹幕 API 保存通知失败");
            return Results.Problem(statusCode: StatusCodes.Status500InternalServerError, title: "通知保存失败");
        }
    }

    private static Dictionary<string, string[]> Validate(DanmakuNotificationRequest request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        var source = request.Source?.Trim();
        var title = request.Title?.Trim();
        var body = request.Body?.Trim();

        if (source?.Length > MaximumSourceLength)
        {
            errors["source"] = [$"来源不能超过 {MaximumSourceLength} 个字符。"];
        }

        if (title?.Length > MaximumTitleLength)
        {
            errors["title"] = [$"标题不能超过 {MaximumTitleLength} 个字符。"];
        }

        if (string.IsNullOrEmpty(body))
        {
            errors["body"] = ["正文不能为空。"];
        }
        else if (body.Length > MaximumBodyLength)
        {
            errors["body"] = [$"正文不能超过 {MaximumBodyLength} 个字符。"];
        }

        if (!Enum.IsDefined(request.Severity))
        {
            errors["severity"] = ["级别必须是 info、warning 或 critical。"];
        }

        return errors;
    }

    private async Task StopActiveApplicationAsync(CancellationToken cancellationToken)
    {
        if (_application is null) return;
        var application = _application;
        _application = null;
        _activePort = null;
        await StopApplicationAsync(application, cancellationToken).ConfigureAwait(false);
        logger.LogInformation("本地弹幕 API 已停止");
    }

    private static async Task StopApplicationAsync(
        WebApplication application,
        CancellationToken cancellationToken)
    {
        try
        {
            await application.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await application.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task DisposeQuietlyAsync(WebApplication application)
    {
        try
        {
            await application.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // 保留启动时的原始异常。
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_application is not null)
        {
            _application.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _application = null;
        }
        _gate.Dispose();
    }
}
