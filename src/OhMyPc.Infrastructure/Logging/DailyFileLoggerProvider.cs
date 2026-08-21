using Microsoft.Extensions.Logging;

namespace OhMyPc.Infrastructure.Logging;

public sealed class DailyFileLoggerProvider : ILoggerProvider
{
    private readonly object _sync = new();

    public ILogger CreateLogger(string categoryName) => new DailyFileLogger(categoryName, Write);
    public void Dispose() { }

    private void Write(string category, LogLevel level, string message, Exception? exception)
    {
        Directory.CreateDirectory(AppPaths.LogDirectory);
        var path = Path.Combine(AppPaths.LogDirectory, $"oh-my-pc-{DateTime.Now:yyyyMMdd}.log");
        var line = $"{DateTimeOffset.Now:O} [{level}] {category}: {message}";
        if (exception is not null) line += $"{Environment.NewLine}{exception}";
        lock (_sync)
        {
            File.AppendAllText(path, line + Environment.NewLine);
        }
    }

    private sealed class DailyFileLogger(string category, Action<string, LogLevel, string, Exception?> write) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning
            || (logLevel >= LogLevel.Information
                && category.StartsWith("OhMyPc.", StringComparison.Ordinal));

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel)) write(category, logLevel, formatter(state, exception), exception);
        }
    }
}
