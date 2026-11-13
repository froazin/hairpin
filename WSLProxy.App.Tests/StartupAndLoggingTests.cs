using System.Globalization;
using System.IO;
using Microsoft.Extensions.Logging;

namespace WSLProxy.App.Tests;

/// <summary>Setting aside a corrupt settings file, and what the file logger writes and keeps.</summary>
[TestClass]
public sealed class StartupAndLoggingTests
{
    private string _directory = string.Empty;

    [TestInitialize]
    public void CreateDirectory() => _directory = Directory.CreateTempSubdirectory("wslproxy-app-tests-").FullName;

    [TestCleanup]
    public void DeleteDirectory() => Directory.Delete(_directory, recursive: true);

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
            StringAssert.StartsWith(renamed, "settings.json.invalid-");
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
        StringAssert.EndsWith(line, " WARN  RelayService: Upstream proxy.example.com failed [InvalidOperationException]");
        Assert.DoesNotContain("secret", line);
    }
}
