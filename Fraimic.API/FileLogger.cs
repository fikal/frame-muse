using Microsoft.Extensions.Logging;

namespace Fraimic.Api;

/// <summary>
/// A small, dependency-free <see cref="ILogger"/> that appends timestamped lines to a file.
/// Thread-safe. Create one via <see cref="FileLoggerProvider"/> or the
/// <see cref="FraimicLog.ToFile"/> convenience helper.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _path;
    private readonly LogLevel _minLevel;
    private readonly object _gate = new();

    public FileLoggerProvider(string path, LogLevel minLevel = LogLevel.Information)
    {
        _path = Path.GetFullPath(path);
        _minLevel = minLevel;
        string? dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose() { }

    private bool IsEnabled(LogLevel level) => level != LogLevel.None && level >= _minLevel;

    private void Append(string line)
    {
        lock (_gate)
        {
            File.AppendAllText(_path, line + Environment.NewLine);
        }
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => provider.IsEnabled(logLevel);

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;

            string message = formatter(state, exception);
            string shortCategory = category.Contains('.') ? category[(category.LastIndexOf('.') + 1)..] : category;
            string line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} [{Level(logLevel)}] {shortCategory}: {message}";
            if (exception is not null)
                line += Environment.NewLine + exception;
            provider.Append(line);
        }

        private static string Level(LogLevel l) => l switch
        {
            LogLevel.Trace => "TRC",
            LogLevel.Debug => "DBG",
            LogLevel.Information => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            LogLevel.Critical => "CRT",
            _ => "???",
        };
    }
}

/// <summary>Convenience helpers for wiring up Fraimic logging.</summary>
public static class FraimicLog
{
    /// <summary>
    /// Create a logger that appends to <paramref name="path"/>. Pass the result straight to
    /// <see cref="FraimicDevice"/>. Keep the returned provider alive for the app's lifetime.
    /// </summary>
    public static (ILogger Logger, FileLoggerProvider Provider) ToFile(
        string path, string category = "Fraimic", LogLevel minLevel = LogLevel.Debug)
    {
        var provider = new FileLoggerProvider(path, minLevel);
        return (provider.CreateLogger(category), provider);
    }
}
