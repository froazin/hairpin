using Microsoft.Extensions.Logging;
using WSLProxy.UI.Diagnostics;

namespace WSLProxy.UI.Tests;

/// <summary>The in-memory log store's bounds, and how the log window filters it.</summary>
[TestClass]
public sealed class LogTests
{
    [TestMethod]
    public void Store_keeps_only_the_newest_lines_and_cuts_long_messages()
    {
        var store = new LogStore(capacity: 3);
        var changes = 0;
        store.Changed += (_, _) => changes++;

        for (var i = 1; i <= 5; i++)
        {
            store.Add(Entry(LogLevel.Information, "line " + i));
        }

        store.Add(Entry(LogLevel.Information, new string('x', LogStore.MaxMessageLength + 50)));

        var kept = store.Snapshot();
        Assert.AreEqual("line 4, line 5", string.Join(", ", kept.Take(2).Select(e => e.Message)));
        Assert.HasCount(LogStore.MaxMessageLength, kept[2].Message);
        StringAssert.EndsWith(kept[2].Message, "…");
        Assert.AreEqual(6, changes);

        store.Clear();
        Assert.IsEmpty(store.Snapshot());
    }

    [TestMethod]
    [DataRow(LogLevel.Trace, null, "DEBUG accepted|INFO GET example.com|WARN upstream down|ERROR crashed")]
    [DataRow(LogLevel.Information, null, "INFO GET example.com|WARN upstream down|ERROR crashed")]
    [DataRow(LogLevel.Warning, null, "WARN upstream down|ERROR crashed")]
    [DataRow(LogLevel.Trace, " EXAMPLE ", "INFO GET example.com")]
    [DataRow(LogLevel.Trace, "relayservice", "DEBUG accepted|WARN upstream down")]
    public void View_filters_by_lowest_level_and_by_text_in_the_message_or_source(LogLevel minimum, string? text, string expected)
    {
        LogEntry[] entries =
        [
            Entry(LogLevel.Debug, "accepted"),
            new(DateTime.UtcNow, LogLevel.Information, "ClientSession", "GET example.com"),
            Entry(LogLevel.Warning, "upstream down"),
            new(DateTime.UtcNow, LogLevel.Error, "TrayApplication", "crashed"),
        ];

        var rows = LogView.Filter(entries, minimum, text);

        Assert.AreEqual(expected, string.Join("|", rows.Select(r => r.Level + " " + r.Message)));
        Assert.AreEqual("Warning", LogView.Filter(entries, LogLevel.Warning, null)[0].Tone);
    }

    private static LogEntry Entry(LogLevel level, string message) => new(DateTime.UtcNow, level, "RelayService", message);
}
