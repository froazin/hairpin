using System.Diagnostics;
using System.Globalization;
using WSLProxy.Relay.Status;

namespace WSLProxy.UI.Diagnostics;

/// <summary>The relay's totals at one moment.</summary>
/// <param name="Elapsed">When it was taken, measured from when monitoring started, so a clock change can't skew rates.</param>
/// <param name="Counters">The totals.</param>
public sealed record TrafficSample(TimeSpan Elapsed, RelayCounters Counters);

/// <summary>What the metrics panel shows, worked out from the samples.</summary>
/// <param name="DownPerSecond">Bytes a second sent back to clients, averaged over the last few seconds.</param>
/// <param name="UpPerSecond">Bytes a second sent towards servers, averaged over the last few seconds.</param>
/// <param name="RequestsLastMinute">Requests handled in the last minute, or since monitoring started if that's sooner.</param>
/// <param name="Latest">The latest totals, or null before the first sample.</param>
/// <param name="Down">Bytes a second down, one point a sample, oldest first, for the chart.</param>
/// <param name="Up">Bytes a second up, one point a sample, oldest first, for the chart.</param>
public sealed record TrafficSummary(
    double DownPerSecond,
    double UpPerSecond,
    long RequestsLastMinute,
    RelayCounters? Latest,
    IReadOnlyList<double> Down,
    IReadOnlyList<double> Up)
{
    /// <summary>How many seconds the rates shown as "now" average over, to smooth out bursts.</summary>
    public const int RateWindowSeconds = 3;

    /// <summary>Works out the summary from samples, oldest first. Pure, so it's tested without a timer or a relay.</summary>
    public static TrafficSummary From(IReadOnlyList<TrafficSample> samples, int chartPoints)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count == 0)
        {
            return new TrafficSummary(0, 0, 0, null, [], []);
        }

        var down = new List<double>();
        var up = new List<double>();
        for (var i = 1; i < samples.Count; i++)
        {
            var seconds = (samples[i].Elapsed - samples[i - 1].Elapsed).TotalSeconds;
            if (seconds > 0)
            {
                down.Add(Rate(samples[i - 1].Counters.BytesDown, samples[i].Counters.BytesDown, seconds));
                up.Add(Rate(samples[i - 1].Counters.BytesUp, samples[i].Counters.BytesUp, seconds));
            }
        }

        var latest = samples[^1];
        var windowStart = Earliest(samples, latest.Elapsed - TimeSpan.FromSeconds(RateWindowSeconds));
        var windowSeconds = (latest.Elapsed - windowStart.Elapsed).TotalSeconds;
        var minuteStart = Earliest(samples, latest.Elapsed - TimeSpan.FromMinutes(1));
        return new TrafficSummary(
            windowSeconds > 0 ? Rate(windowStart.Counters.BytesDown, latest.Counters.BytesDown, windowSeconds) : 0,
            windowSeconds > 0 ? Rate(windowStart.Counters.BytesUp, latest.Counters.BytesUp, windowSeconds) : 0,
            Math.Max(0, latest.Counters.Requests - minuteStart.Counters.Requests),
            latest.Counters,
            down.Count > chartPoints ? down[^chartPoints..] : down,
            up.Count > chartPoints ? up[^chartPoints..] : up);
    }

    // The counters only grow, but never show a negative rate if one ever doesn't.
    private static double Rate(long from, long to, double seconds) => Math.Max(0, to - from) / seconds;

    // The oldest sample taken at or after the cut-off, so a window never reaches past the history.
    private static TrafficSample Earliest(IReadOnlyList<TrafficSample> samples, TimeSpan cutOff)
    {
        foreach (var sample in samples)
        {
            if (sample.Elapsed >= cutOff)
            {
                return sample;
            }
        }

        return samples[^1];
    }
}

/// <summary>Formats byte counts for people, in binary units as File Explorer shows them.</summary>
public static class ByteFormat
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    /// <summary>Such as <c>0 B</c>, <c>512 B</c>, <c>1.5 KB</c>, <c>12.3 MB</c> or <c>240 MB</c>.</summary>
    public static string Size(double bytes)
    {
        var value = Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        var format = unit == 0 || value >= 100 ? "N0" : "N1";
        return value.ToString(format, CultureInfo.CurrentCulture) + " " + Units[unit];
    }

    /// <summary>Such as <c>1.5 MB/s</c>.</summary>
    public static string Rate(double bytesPerSecond) => Size(bytesPerSecond) + "/s";
}

/// <summary>
/// Samples the relay's totals once a second into a <see cref="RingBuffer{T}"/> of the last five minutes, so the metrics
/// panel can show rates and a chart, including the history from before the flyout opened. It holds 300 small samples,
/// whatever the traffic.
/// </summary>
public sealed class TrafficMonitor : IDisposable
{
    /// <summary>How many samples it keeps: five minutes at one a second.</summary>
    public const int Capacity = 300;

    private readonly IRelayMetrics _metrics;
    private readonly RingBuffer<TrafficSample> _samples = new(Capacity);
    private readonly long _started = Stopwatch.GetTimestamp();
    private readonly Timer _timer;

    /// <summary>Starts sampling <paramref name="metrics"/> straight away.</summary>
    public TrafficMonitor(IRelayMetrics metrics)
    {
        _metrics = metrics;
        _timer = new Timer(_ => Sample(), null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
    }

    /// <summary>Raised on a thread-pool thread after each sample.</summary>
    public event EventHandler? Sampled;

    /// <summary>The summary of the samples so far.</summary>
    public TrafficSummary Summary(int chartPoints) => TrafficSummary.From(_samples.ToArray(), chartPoints);

    /// <inheritdoc />
    public void Dispose() => _timer.Dispose();

    private void Sample()
    {
        _samples.Add(new TrafficSample(Stopwatch.GetElapsedTime(_started), _metrics.Read()));
        Sampled?.Invoke(this, EventArgs.Empty);
    }
}
