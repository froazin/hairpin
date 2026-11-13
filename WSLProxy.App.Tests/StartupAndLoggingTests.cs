using System.Globalization;
using System.IO;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WSLProxy.Relay;
using WSLProxy.Relay.Configuration;
using WSLProxy.UI.Diagnostics;
using WSLProxy.UI.Settings;

namespace WSLProxy.App.Tests;

/// <summary>Setting aside a corrupt settings file, binding a saved one, what the loggers write and keep, and the debug logging switch.</summary>
[TestClass]
[OSCondition(OperatingSystems.Windows)]
public sealed class StartupAndLoggingTests
{
    private string _directory = string.Empty;

    [TestInitialize]
    public void CreateDirectory() => _directory = Directory.CreateTempSubdirectory("wslproxy-app-tests-").FullName;

    [TestCleanup]
    public void DeleteDirectory() => Directory.Delete(_directory, recursive: true);

    [TestMethod]
    [DataRow(null, @"C:\Users\someone\AppData\Local\WSLProxy", DisplayName = "Zip build")]
    [DataRow("WSLProxyRelay_1a2b3c4d5e6f7", @"C:\Users\someone\AppData\Local\Packages\WSLProxyRelay_1a2b3c4d5e6f7\LocalState", DisplayName = "MSIX package")]
    public void The_packaged_app_keeps_its_files_in_its_package_folder_and_the_zip_build_in_its_own(string? familyName, string expected) =>
        Assert.AreEqual(expected, Program.DataDirectory(@"C:\Users\someone\AppData\Local", familyName));

    [TestMethod]
    [DataRow("{ \"Relay\": { \"ListenPort\": 3129, }, // A trailing comma and a comment are fine\n}", false)]
    [DataRow("{ \"Relay\": { \"ListenPort\": ", true)]
    [DataRow(null, false, DisplayName = "No file")]
    public void A_settings_file_that_is_not_json_is_renamed_so_the_app_starts_with_defaults(string? content, bool setAside)
    {
        var path = Path.Combine(_directory, "settings.json");
        if (content is not null)
        {
            File.WriteAllText(path, content);
        }

        var renamed = Program.SetAsideIfCorrupt(path);

        Assert.AreEqual(setAside, renamed is not null);
        Assert.AreEqual(content is not null && !setAside, File.Exists(path));
        if (renamed is not null)
        {
            Assert.StartsWith("settings.json.invalid-", renamed);
            Assert.IsTrue(File.Exists(Path.Combine(_directory, renamed)));
        }
    }

    [TestMethod]
    public void A_settings_file_another_program_has_locked_is_left_alone_so_the_app_still_starts()
    {
        var path = Path.Combine(_directory, "settings.json");
        File.WriteAllText(path, "{ not json");
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.IsNull(Program.SetAsideIfCorrupt(path));
        }

        Assert.IsTrue(File.Exists(path));
    }

    [TestMethod]
    public void Saved_settings_bind_over_the_defaults_with_the_https_proxy_borrowing_the_http_one()
    {
        var path = Path.Combine(_directory, "settings.json");
        var saved = new UserSettingsStore(path).Save(
            new UserSettings(3129, "proxy.example.com", 8080, "", null, ["corp.example"], DenyLocalDestinations: true, StartAtLogin: false),
            new RelayOptions());
        Assert.IsEmpty(saved);

        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"))
            .AddJsonFile(path)
            .Build();
        using var services = new ServiceCollection().AddLogging().AddRelay(configuration).BuildServiceProvider();
        var options = services.GetRequiredService<IOptionsMonitor<RelayOptions>>().CurrentValue;

        Assert.IsNull(options.ConfigurationError);
        Assert.AreEqual(new UpstreamEndpoint("proxy.example.com", 8080), options.Upstream.ForHttp);
        Assert.AreEqual(new UpstreamEndpoint("proxy.example.com", 8080), options.Upstream.ForHttps);
        Assert.AreEqual(3129, options.ListenPort);
    }

    [TestMethod]
    public void File_logger_writes_exceptions_by_type_only_and_deletes_files_past_retention()
    {
        var old = Path.Combine(_directory, "relay-20000101.log");
        File.WriteAllText(old, "old");
        var provider = new FileLoggerProvider(_directory, retentionDays: 7);

        provider.CreateLogger("WSLProxy.Relay.Proxy.RelayService")
            .LogWarning(new InvalidOperationException("secret.example.com/path?token=1"), "Upstream {Host} failed", "proxy.example.com");
        provider.Dispose();

        Assert.IsFalse(File.Exists(old));
        var today = Path.Combine(_directory, "relay-" + DateTime.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".log");
        var line = File.ReadAllText(today).TrimEnd();
        Assert.EndsWith(" WARN  RelayService: Upstream proxy.example.com failed [InvalidOperationException]", line);
        Assert.DoesNotContain("secret", line);
    }

    [TestMethod]
    public void Debug_switch_turns_debug_lines_on_and_off_live_and_the_log_window_gets_exception_types_only()
    {
        var store = new LogStore(capacity: 10);
        var debug = new DebugLoggingSwitch();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Logging:LogLevel:Default"] = "Information" })
            .Add(debug)
            .Build();
        using var services = new ServiceCollection()
            .AddLogging(logging => logging.AddConfiguration(configuration.GetSection("Logging")).AddProvider(new MemoryLoggerProvider(store)))
            .BuildServiceProvider();
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("WSLProxy.Relay.Proxy.ClientSession");

        logger.LogDebug("before");
        debug.SetEnabled(true);
        logger.LogDebug("while on");
        debug.SetEnabled(false);
        logger.LogDebug("after");
        logger.LogError(new InvalidOperationException("secret.example.com"), "failed");

        Assert.AreEqual("ClientSession while on|ClientSession failed [InvalidOperationException]",
            string.Join("|", store.Snapshot().Select(e => e.Category + " " + e.Message)));
        Assert.IsFalse(debug.IsEnabled);
    }
}
