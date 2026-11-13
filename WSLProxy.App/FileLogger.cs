using System.IO;
using System.Globalization;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace WSLProxy.App;

/// <summary>
/// Writes log lines to one file per day in the log folder, from a background task so logging never blocks the relay.
/// Deletes files older than the retention period. No third-party logging library.
/// </summary>
internal sealed class FileLoggerProvider : ILoggerProvider
{
    private const int QueueCapacity = 10_000;

    private readonly string _directory;
    private readonly int _retentionDays;
    private readonly Channel<string> _queue = Channel.CreateBounded<string>(
        new BoundedChannelOptions(QueueCapacity) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly Task _writer;

    public FileLoggerProvider(string directory, int retentionDays)
    {
        _directory = directory;
        _retentionDays = Math.Clamp(retentionDays, 1, 365);
        Directory.CreateDirectory(directory);
        _writer = Task.Run(WriteLoopAsync);
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, ShortName(categoryName));

    public void Dispose()
    {
        _queue.Writer.TryComplete();
        _writer.Wait(TimeSpan.FromSeconds(2));
    }

    private void Enqueue(string line) => _queue.Writer.TryWrite(line);

    private async Task WriteLoopAsync()
    {
        StreamWriter? writer = null;
        var day = DateOnly.MinValue;
        await foreach (var line in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                var today = DateOnly.FromDateTime(DateTime.UtcNow);
                if (writer is null || today != day)
                {
                    if (writer is not null)
                    {
                        await writer.DisposeAsync().ConfigureAwait(false);
                    }

                    writer = null;
                    day = today;
                    DeleteOldFiles(today);
                    writer = Open(today);
                }

                await writer.WriteLineAsync(line).ConfigureAwait(false);
                if (_queue.Reader.Count == 0)
                {
                    await writer.FlushAsync().ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Drop this line and try to reopen the file for the next one, so one bad moment doesn't end logging.
                if (writer is not null)
                {
                    try
                    {
                        await writer.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (Exception disposeEx) when (disposeEx is IOException or UnauthorizedAccessException)
                    {
                        // Already broken.
                    }
                }

                writer = null;
            }
        }

        if (writer is not null)
        {
            await writer.DisposeAsync().ConfigureAwait(false);
        }
    }

    private StreamWriter Open(DateOnly day)
    {
        var path = Path.Combine(_directory, "relay-" + day.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".log");
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        return new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private void DeleteOldFiles(DateOnly today)
    {
        var cutoff = today.AddDays(-_retentionDays).ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        foreach (var file in Directory.EnumerateFiles(_directory, "relay-*.log"))
        {
            var stamp = Path.GetFileNameWithoutExtension(file)["relay-".Length..];
            if (string.CompareOrdinal(stamp, cutoff) < 0)
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // In use or protected; try again tomorrow.
                }
            }
        }
    }

    private static string ShortName(string category)
    {
        var dot = category.LastIndexOf('.');
        return dot >= 0 ? category[(dot + 1)..] : category;
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            // Exceptions are logged by type only: their messages can carry peer data.
            var suffix = exception is null ? string.Empty : " [" + exception.GetType().Name + "]";
            var line = string.Create(CultureInfo.InvariantCulture,
                $"{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ} {Level(logLevel)} {category}: {formatter(state, exception)}{suffix}");
            provider.Enqueue(line);
        }

        private static string Level(LogLevel level) => level switch
        {
            LogLevel.Trace => "TRACE",
            LogLevel.Debug => "DEBUG",
            LogLevel.Information => "INFO ",
            LogLevel.Warning => "WARN ",
            LogLevel.Error => "ERROR",
            _ => "CRIT ",
        };
    }
}
