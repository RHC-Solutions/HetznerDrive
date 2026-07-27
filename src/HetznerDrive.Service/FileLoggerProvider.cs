using HetznerDrive.Core;
using Microsoft.Extensions.Logging;

namespace HetznerDrive.Service;

/// <summary>
/// Bridges <see cref="ILogger"/> onto the app's existing <see cref="FileLogger"/>, so service logs
/// land next to the tray app's in the same daily-rolling, self-pruning format an administrator is
/// already looking at — rather than introducing a second logging stack for one consumer.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly FileLogger _logger;

    public FileLoggerProvider(string logsDir) => _logger = new FileLogger(logsDir);

    public ILogger CreateLogger(string categoryName) => new Adapter(_logger, categoryName);

    public void Dispose() => _logger.Dispose();

    private sealed class Adapter : ILogger
    {
        private readonly FileLogger _sink;
        private readonly string _category;

        public Adapter(FileLogger sink, string category)
        {
            _sink = sink;
            // Only the leaf type name is useful in a log line; the full namespace is noise.
            var lastDot = category.LastIndexOf('.');
            _category = lastDot >= 0 ? category[(lastDot + 1)..] : category;
        }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            var message = formatter(state, exception);
            if (exception is not null) message += $" -- {exception.GetType().Name}: {exception.Message}";
            _sink.Log($"{Abbreviate(logLevel)} {_category}: {message}");
        }

        private static string Abbreviate(LogLevel level) => level switch
        {
            LogLevel.Critical => "CRITICAL:",
            LogLevel.Error => "ERROR   :",
            LogLevel.Warning => "WARN    :",
            _ => "INFO    :",
        };
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}
