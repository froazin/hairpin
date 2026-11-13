using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using WSLProxy.UI.Diagnostics;

namespace WSLProxy.App;

/// <summary>How every log destination formats a line, so the log files and the log window say the same thing.</summary>
internal static class LogFormat
{
    /// <summary>
    /// The message, with an exception's type appended in brackets. Never the exception's message or stack trace: they can
    /// carry hosts, paths or other peer data.
    /// </summary>
    public static string Message<TState>(TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);
        var message = formatter(state, exception);
        return exception is null ? message : message + " [" + exception.GetType().Name + "]";
    }

    /// <summary>The last part of a category, such as <c>RelayService</c> for <c>WSLProxy.Relay.Proxy.RelayService</c>.</summary>
    public static string ShortName(string category)
    {
        ArgumentNullException.ThrowIfNull(category);
        var dot = category.LastIndexOf('.');
        return dot >= 0 ? category[(dot + 1)..] : category;
    }

    /// <summary>A fixed-width level name.</summary>
    public static string Level(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRACE",
        LogLevel.Debug => "DEBUG",
        LogLevel.Information => "INFO ",
        LogLevel.Warning => "WARN ",
        LogLevel.Error => "ERROR",
        _ => "CRIT ",
    };
}

/// <summary>Keeps recent log lines in a <see cref="LogStore"/> for the log window.</summary>
internal sealed class MemoryLoggerProvider(LogStore store) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new MemoryLogger(store, LogFormat.ShortName(categoryName));

    public void Dispose()
    {
        // The store belongs to the app, not to this provider.
    }

    private sealed class MemoryLogger(LogStore store, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                store.Add(new LogEntry(DateTime.UtcNow, logLevel, category, LogFormat.Message(state, exception, formatter)));
            }
        }
    }
}

/// <summary>
/// The tray's debug logging switch. It's the last configuration source, so while it's on, its <c>Logging:LogLevel</c>
/// values override the settings files, and turning it on or off reloads the logging filters straight away. It holds its
/// values only in memory, so debug logging is always off when the app starts.
/// </summary>
internal sealed class DebugLoggingSwitch : ConfigurationProvider, IConfigurationSource, IDebugLogging
{
    private readonly Lock _lock = new();

    public bool IsEnabled { get; private set; }

    public IConfigurationProvider Build(IConfigurationBuilder builder) => this;

    public void SetEnabled(bool enabled)
    {
        lock (_lock)
        {
            if (enabled == IsEnabled)
            {
                return;
            }

            IsEnabled = enabled;
            Data = enabled
                ? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Logging:LogLevel:Default"] = nameof(LogLevel.Debug),
                    ["Logging:LogLevel:WSLProxy"] = nameof(LogLevel.Debug),
                }
                : new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        }

        OnReload();
    }
}
