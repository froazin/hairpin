using System.Globalization;
using WSLProxy.Relay.Status;
using WSLProxy.UI.Diagnostics;

namespace WSLProxy.UI.Tests;

/// <summary>How the metrics panel turns once-a-second samples of the relay's totals into rates, a chart and readable sizes.</summary>
[TestClass]
[OSCondition(OperatingSystems.Windows)]
public sealed class TrafficTests
{
    [TestMethod]
    public void Summary_averages_recent_rates_counts_the_last_minute_and_keeps_the_newest_chart_points()
    {
        // 70 seconds of samples: 1 KB down and 100 B up a second, and a request every second.
        var samples = Enumerable.Range(0, 71)
            .Select(s => Sample(s, requests: s, down: s * 1024L, up: s * 100L))
            .ToList();

        // A 3 KB burst down in the last second.
        samples.Add(Sample(71, requests: 71, down: (70 * 1024L) + (4 * 1024), up: 71 * 100L));

        var summary = TrafficSummary.From(samples, chartPoints: 5);

        Assert.AreEqual(2048, summary.DownPerSecond, "(1 KB + 1 KB + 4 KB) over the last 3 seconds.");
        Assert.AreEqual(100, summary.UpPerSecond);
        Assert.AreEqual(60, summary.RequestsLastMinute);
        Assert.AreEqual("1024,1024,1024,1024,4096", string.Join(",", summary.Down));
        Assert.HasCount(5, summary.Up);
        Assert.AreEqual(71, summary.Latest!.Requests);
    }

    [TestMethod]
    public void Summary_of_too_few_samples_is_all_zero()
    {
        Assert.IsNull(TrafficSummary.From([], 120).Latest);

        var one = TrafficSummary.From([Sample(0, 5, 100, 100)], 120);
        Assert.AreEqual(0, one.DownPerSecond);
        Assert.AreEqual(0, one.RequestsLastMinute);
        Assert.IsEmpty(one.Down);
    }

    [TestMethod]
    [DataRow(0d, "0 B")]
    [DataRow(512d, "512 B")]
    [DataRow(1536d, "1.5 KB")]
    [DataRow(12.34 * 1024 * 1024, "12.3 MB")]
    [DataRow(240d * 1024 * 1024, "240 MB")]
    [DataRow(-5d, "0 B")]
    public void Sizes_use_binary_bytes_with_one_decimal_below_100(double bytes, string expected)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            Assert.AreEqual(expected, TrafficFormat.Size(bytes));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [TestMethod]
    [DataRow(0d, "0 b/s")]
    [DataRow(100d, "800 b/s")]
    [DataRow(1_230_000d / 8, "1.2 Mb/s")]
    [DataRow(12_340_000d / 8, "12.3 Mb/s")]
    [DataRow(1.5e9 / 8, "1.5 Gb/s")]
    public void Speeds_use_decimal_bits_a_second(double bytesPerSecond, string expected)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            Assert.AreEqual(expected, TrafficFormat.Speed(bytesPerSecond));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [TestMethod]
    public void Error_rate_counts_only_the_last_two_minutes_and_is_empty_when_there_were_no_requests()
    {
        // 10 errors in 100 requests early on, then 50 requests and 1 error in the last two minutes.
        List<TrafficSample> samples =
        [
            Sample(0, 0, 0, 0),
            Sample(10, 100, 0, 0, errors: 10),
            Sample(85, 100, 0, 0, errors: 10),
            Sample(200, 150, 0, 0, errors: 11),
        ];

        Assert.AreEqual(0.02, TrafficSummary.From(samples, 120).ErrorRate);

        // Two quiet minutes later, nothing is counted.
        samples.Add(Sample(330, 150, 0, 0, errors: 11));
        var quiet = TrafficSummary.From(samples, 120);
        Assert.IsNull(quiet.ErrorRate);
        Assert.AreEqual("–", TrafficFormat.Percent(quiet.ErrorRate));
        Assert.AreEqual("2%", TrafficFormat.Percent(0.02));
        Assert.AreEqual("0.5%", TrafficFormat.Percent(0.005));
        Assert.AreEqual("0%", TrafficFormat.Percent(0));
        Assert.AreEqual("<0.1%", TrafficFormat.Percent(1d / 3000));
    }

    private static TrafficSample Sample(int second, long requests, long down, long up, long errors = 0) =>
        new(TimeSpan.FromSeconds(second), new RelayCounters(requests, requests - errors, 0, errors, up, down, 1));
}
