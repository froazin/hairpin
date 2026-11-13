using System.Globalization;
using Microsoft.Extensions.Logging;

namespace WSLProxy.UI.Diagnostics;

/// <summary>A log line as the log window shows it.</summary>
/// <param name="Time">The local time, such as <c>14:03:07.512</c>.</param>
/// <param name="Level">The level's short name, such as <c>WARN</c>.</param>
/// <param name="Source">The class that logged it.</param>
/// <param name="Message">The message.</param>
/// <param name="Tone">How the window colours the line: <c>Quiet</c>, <c>Warning</c>, <c>Error</c> or empty.</param>
public sealed record LogRow(string Time, string Level, string Source, string Message, string Tone)
{
    /// <summary>The line as plain text, for the clipboard.</summary>
    public override string ToString() => Time + " " + Level + " " + Source + ": " + Message;
}

/// <summary>Filters log lines for the log window. Pure, so it's tested without a window.</summary>
public static class LogView
{
    /// <summary>
    /// The lines at <paramref name="minimum"/> or above whose source or message contains <paramref name="text"/>, ignoring
    /// case, oldest first.
    /// </summary>
    public static IReadOnlyList<LogRow> Filter(IEnumerable<LogEntry> entries, LogLevel minimum, string? text)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var find = text?.Trim();
        var rows = new List<LogRow>();
        foreach (var entry in entries)
        {
            if (entry.Level < minimum)
            {
                continue;
            }

            if (!string.IsNullOrEmpty(find)
                && !entry.Message.Contains(find, StringComparison.OrdinalIgnoreCase)
                && !entry.Category.Contains(find, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            rows.Add(new LogRow(
                entry.Time.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.CurrentCulture),
                Name(entry.Level),
                entry.Category,
                entry.Message,
                Tone(entry.Level)));
        }

        return rows;
    }

    private static string Name(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRACE",
        LogLevel.Debug => "DEBUG",
        LogLevel.Information => "INFO",
        LogLevel.Warning => "WARN",
        LogLevel.Error => "ERROR",
        _ => "CRIT",
    };

    private static string Tone(LogLevel level) => level switch
    {
        LogLevel.Trace or LogLevel.Debug => "Quiet",
        LogLevel.Warning => "Warning",
        LogLevel.Error or LogLevel.Critical => "Error",
        _ => string.Empty,
    };
}
