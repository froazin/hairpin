using Microsoft.Extensions.Logging;

namespace WSLProxy.UI.Diagnostics;

/// <summary>One log line held in memory for the log window.</summary>
/// <param name="Time">When it was logged, in UTC.</param>
/// <param name="Level">Its level.</param>
/// <param name="Category">The short name of the class that logged it, such as <c>RelayService</c>.</param>
/// <param name="Message">The formatted message, already free of anything that mustn't be logged.</param>
public sealed record LogEntry(DateTime Time, LogLevel Level, string Category, string Message);

/// <summary>
/// The most recent log lines, in memory, for the log window. It holds at most <see cref="RingBuffer{T}.Capacity"/> lines
/// and cuts each message to <see cref="MaxMessageLength"/> characters, so it stays small: about 2 MB at the most with the
/// default 1,000 lines. The log files keep the full history.
/// </summary>
/// <param name="capacity">How many of the newest lines to keep.</param>
public sealed class LogStore(int capacity)
{
    /// <summary>The longest message kept, in characters. Longer messages are cut and end with an ellipsis.</summary>
    public const int MaxMessageLength = 1000;

    private readonly RingBuffer<LogEntry> _entries = new(capacity);

    /// <summary>
    /// Raised after each line is added, on the thread that logged it. Handlers must be quick and mustn't log, or they'd
    /// slow down or recurse into the code that logged.
    /// </summary>
    public event EventHandler? Changed;

    /// <summary>The most lines the store keeps.</summary>
    public int Capacity => _entries.Capacity;

    /// <summary>Adds a line, dropping the oldest when the store is full.</summary>
    public void Add(LogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Message.Length > MaxMessageLength)
        {
            entry = entry with { Message = string.Concat(entry.Message.AsSpan(0, MaxMessageLength - 1), "…") };
        }

        _entries.Add(entry);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The lines held now, oldest first.</summary>
    public IReadOnlyList<LogEntry> Snapshot() => _entries.ToArray();

    /// <summary>Removes every line from memory. The log files aren't touched.</summary>
    public void Clear()
    {
        _entries.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>Turns debug logging on and off while the app runs. It's always off when the app starts.</summary>
public interface IDebugLogging
{
    /// <summary>True while debug logging is on.</summary>
    bool IsEnabled { get; }

    /// <summary>Turns debug logging on or off for every log destination, straight away.</summary>
    void SetEnabled(bool enabled);
}
