using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using OhMyPc.Core;
using OhMyPc.Core.Domain;
using OhMyPc.Infrastructure.LocalApi;

namespace OhMyPc.IntegrationTests;

public sealed class LocalNotificationApiTests : IAsyncLifetime
{
    private readonly CaptureNotificationSink _notifications = new();
    private LocalNotificationApiService _service = null!;
    private HttpClient _client = null!;
    private int _port;

    public async Task InitializeAsync()
    {
        _port = GetAvailablePort();
        _service = new LocalNotificationApiService(
            _notifications,
            NullLogger<LocalNotificationApiService>.Instance);
        await _service.ApplySettingsAsync(new AppSettings
        {
            LocalApiEnabled = true,
            LocalApiPort = _port
        });
        _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_port}") };
    }

    [Fact]
    public async Task DanmakuEndpoint_PublishesValidatedNotification()
    {
        using var response = await _client.PostAsJsonAsync(
            LocalNotificationApiService.DanmakuPath,
            new
            {
                source = "build-agent",
                title = "构建任务",
                body = "测试已经通过",
                severity = "warning"
            });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var message = Assert.Single(_notifications.Messages);
        var record = Assert.Single(_notifications.Records);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(record.Id, payload.RootElement.GetProperty("id").GetString());
        Assert.Equal(record.CreatedAt, payload.RootElement.GetProperty("createdAt").GetDateTimeOffset());
        Assert.Equal(NotificationOrigin.LocalApi, message.Origin);
        Assert.Equal("build-agent", message.Source);
        Assert.Equal("构建任务", message.Title);
        Assert.Equal("测试已经通过", message.Body);
        Assert.Equal(NotificationChannels.Danmaku, message.Channels);
        Assert.Equal(NotificationSeverity.Warning, message.Severity);
        Assert.Equal("local-api", message.SubjectKey);
    }

    [Fact]
    public async Task DanmakuEndpoint_RejectsEmptyBody()
    {
        using var response = await _client.PostAsJsonAsync(
            LocalNotificationApiService.DanmakuPath,
            new { body = "   " });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_notifications.Messages);
    }

    [Fact]
    public async Task DanmakuEndpoint_RejectsUnknownSeverity()
    {
        using var response = await _client.PostAsJsonAsync(
            LocalNotificationApiService.DanmakuPath,
            new { body = "测试", severity = "unknown" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_notifications.Messages);
    }

    [Fact]
    public async Task DanmakuEndpoint_DefaultsBlankSourceAndRejectsOverlongSource()
    {
        using var accepted = await _client.PostAsJsonAsync(
            LocalNotificationApiService.DanmakuPath,
            new { source = "   ", body = "测试" });
        using var rejected = await _client.PostAsJsonAsync(
            LocalNotificationApiService.DanmakuPath,
            new { source = new string('s', 81), body = "测试" });

        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        Assert.Equal("local-api", Assert.Single(_notifications.Messages).Source);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
    }

    [Fact]
    public async Task DanmakuEndpoint_ReturnsServerErrorWhenPersistenceFails()
    {
        _notifications.ThrowOnPublish = true;

        using var response = await _client.PostAsJsonAsync(
            LocalNotificationApiService.DanmakuPath,
            new { body = "测试" });

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Empty(_notifications.Messages);
    }

    [Fact]
    public async Task DanmakuEndpoint_RateLimitsSixtyFirstRequest()
    {
        HttpStatusCode lastStatus = default;
        for (var index = 0; index < 61; index++)
        {
            using var response = await _client.PostAsJsonAsync(
                LocalNotificationApiService.DanmakuPath,
                new { body = $"测试 {index}" });
            lastStatus = response.StatusCode;
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, lastStatus);
        Assert.Equal(60, _notifications.Messages.Count);
    }

    [Fact]
    public async Task OccupiedReplacementPort_PreservesExistingListener()
    {
        var blocker = new TcpListener(IPAddress.Loopback, 0);
        blocker.Start();
        try
        {
            var blockedPort = ((IPEndPoint)blocker.LocalEndpoint).Port;
            await Assert.ThrowsAsync<LocalNotificationApiException>(() =>
                _service.ApplySettingsAsync(new AppSettings
                {
                    LocalApiEnabled = true,
                    LocalApiPort = blockedPort
                }));

            Assert.True(_service.IsRunning);
            Assert.Equal(_port, _service.ActivePort);
            using var response = await _client.GetAsync(LocalNotificationApiService.HealthPath);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            blocker.Stop();
        }
    }

    [Fact]
    public async Task DisabledSetting_StopsListener()
    {
        await _service.ApplySettingsAsync(new AppSettings { LocalApiEnabled = false });

        Assert.False(_service.IsRunning);
        Assert.Null(_service.ActivePort);
    }

    [Fact]
    public async Task HealthEndpoint_ReportsReady()
    {
        using var response = await _client.GetAsync(LocalNotificationApiService.HealthPath);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("{\"status\":\"ok\"}", await response.Content.ReadAsStringAsync());
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _service.StopAsync(CancellationToken.None);
        _service.Dispose();
    }

    private static int GetAvailablePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private sealed class CaptureNotificationSink : INotificationSink
    {
        public List<NotificationMessage> Messages { get; } = [];
        public List<NotificationRecord> Records { get; } = [];
        public bool ThrowOnPublish { get; set; }

        public Task<NotificationRecord> PublishAsync(NotificationMessage message, CancellationToken cancellationToken = default)
        {
            if (ThrowOnPublish) throw new InvalidOperationException("保存失败");
            Messages.Add(message);
            var record = ToRecord(message);
            Records.Add(record);
            return Task.FromResult(record);
        }

        private static NotificationRecord ToRecord(NotificationMessage message) => new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Origin = message.Origin,
            Source = message.Source,
            Title = message.Title,
            Body = message.Body,
            Channels = message.Channels,
            Severity = message.Severity,
            SubjectKey = message.SubjectKey,
            CreatedAt = DateTimeOffset.UtcNow
        };
    }
}
