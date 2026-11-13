using System.IO;
using System.Text.Json.Nodes;
using WSLProxy.Relay.Configuration;
using WSLProxy.UI.Settings;

namespace WSLProxy.UI.Tests;

/// <summary>Saving the user's settings file, and reading what the user types into the bypass list.</summary>
[TestClass]
public sealed class SettingsTests
{
    private string _directory = string.Empty;

    [TestInitialize]
    public void CreateDirectory() => _directory = Directory.CreateTempSubdirectory("wslproxy-ui-tests-").FullName;

    [TestCleanup]
    public void DeleteDirectory() => Directory.Delete(_directory, recursive: true);

    [TestMethod]
    public void Save_writes_the_relay_and_app_sections_and_keeps_keys_it_does_not_own()
    {
        var path = Path.Combine(_directory, "settings.json");

        // A hand-added limit and an unknown section, with keys cased differently from the options classes, and the single
        // upstream proxy that earlier versions wrote.
        File.WriteAllText(path, """
            { "relay": { "limits": { "MaxConnections": 64 }, "upstream": { "host": "old.example", "port": 3129 } }, "Other": { "Keep": true } }
            """);
        var settings = new UserSettings(3129, " proxy.example.com ", 8080, "", null, ["corp.example", "10.0.0.0/8"],
            DenyLocalDestinations: false, StartAtLogin: false);

        var problems = new UserSettingsStore(path).Save(settings, new RelayOptions());

        Assert.IsEmpty(problems);
        var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.HasCount(3, root, "The existing relay section is updated, not duplicated under another casing.");
        var relay = root["relay"]!;
        Assert.AreEqual(3129, (int)relay["ListenPort"]!);
        Assert.AreEqual("""{"Http":{"Host":"proxy.example.com","Port":8080},"Https":{"Host":"","Port":null}}""",
            relay["upstream"]!.ToJsonString(), "The HTTPS proxy is left to default to the HTTP one, and the old single proxy is gone.");
        Assert.AreEqual("""["corp.example","10.0.0.0/8"]""", relay["NoProxy"]!.ToJsonString());
        Assert.IsFalse((bool)relay["DenyLocalDestinations"]!);
        Assert.AreEqual(64, (int)relay["limits"]!["MaxConnections"]!);
        Assert.IsFalse((bool)root["App"]!["StartAtLogin"]!);
        Assert.IsTrue((bool)root["Other"]!["Keep"]!);
        Assert.IsFalse(File.Exists(path + ".tmp"));
    }

    [TestMethod]
    public void Save_refuses_invalid_settings_and_leaves_the_file_alone()
    {
        var path = Path.Combine(_directory, "settings.json");
        var settings = new UserSettings(3128, "proxy.example.com", 8080, "", 0, [], DenyLocalDestinations: true, StartAtLogin: true);

        var problems = new UserSettingsStore(path).Save(settings, new RelayOptions());

        Assert.HasCount(1, problems);
        StringAssert.Contains(problems[0], "Upstream:Https:Port");
        Assert.IsFalse(File.Exists(path));
    }

    [TestMethod]
    public void Bypass_input_splits_a_pasted_no_proxy_value_normalises_and_skips_duplicates()
    {
        var input = BypassEntries.Read("Example.COM., Corp.Example;10.0.0.0/8 example.com\nhost%25.example, ::1", ["corp.example"]);

        Assert.AreEqual("example.com, 10.0.0.0/8", string.Join(", ", input.Added));
        Assert.AreEqual("host%25.example, ::1", string.Join(", ", input.Rejected));
        Assert.HasCount(2, input.Problems);
        StringAssert.StartsWith(input.Problems[1], "::1: ");
    }
}
