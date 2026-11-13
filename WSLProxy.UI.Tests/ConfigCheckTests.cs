using WSLProxy.UI.Diagnostics;

namespace WSLProxy.UI.Tests;

/// <summary>The checks for the settings that stop WSL using mirrored mode, read the way WSL reads them.</summary>
[TestClass]
[OSCondition(OperatingSystems.Windows)]
public sealed class ConfigCheckTests
{
    [TestMethod]
    [DataRow(null, false)]
    [DataRow(0, false)]
    [DataRow(0x20, false, DisplayName = "Prefer IPv4: the recommended value")]
    [DataRow(0x11, false, DisplayName = "IPv6 off on interfaces, but WSL only refuses 0xFF")]
    [DataRow(0xFF, true)]
    public void Ipv6_is_a_problem_only_when_fully_disabled(int? disabledComponents, bool problem)
    {
        Assert.AreEqual(problem, ConfigChecks.CheckIpv6(disabledComponents) is not null);
    }

    [TestMethod]
    [DataRow("[wsl2]\nnetworkingMode=mirrored\n", "mirrored")]
    [DataRow("[WSL2]\r\nNetworkingMode = \"Mirrored\" # comment\r\n", "Mirrored")]
    [DataRow("[experimental]\nnetworkingMode=mirrored", "mirrored", DisplayName = "The older section still counts")]
    [DataRow("[wsl2]\nnetworkingMode=nat\n[experimental]\nnetworkingMode=mirrored", "mirrored", DisplayName = "The last value wins")]
    [DataRow("[wsl2]\n# networkingMode=mirrored\nmemory=8GB", null, DisplayName = "Commented out")]
    [DataRow("[boot]\nnetworkingMode=mirrored", null, DisplayName = "Wrong section")]
    [DataRow("networkingMode=mirrored", null, DisplayName = "No section")]
    [DataRow("", null)]
    public void Wslconfig_networking_mode_is_read_as_wsl_reads_it(string text, string? expected)
    {
        Assert.AreEqual(expected, ConfigChecks.ReadNetworkingMode(text));
    }

    [TestMethod]
    [DataRow("mirrored", null, null, null)]
    [DataRow("MIRRORED", 1, 1, null, DisplayName = "Users may choose, so the file wins over the default")]
    [DataRow(null, 3, null, null, DisplayName = "No file, policy default is mirrored")]
    [DataRow(null, null, null, "WSL is set to NAT", DisplayName = "No file means NAT")]
    [DataRow("nat", null, null, "WSL is set to NAT")]
    [DataRow("virtioproxy", null, null, "WSL is set to consomme", DisplayName = "The old name for consomme")]
    [DataRow("sideways", null, null, "WSL is set to NAT", DisplayName = "An unknown value is ignored")]
    [DataRow(null, 2, null, "WSL is set to NAT", DisplayName = "Bridged can't be the default")]
    [DataRow("mirrored", 1, 0, "Your organisation's WSL policy sets the networking mode to NAT", DisplayName = "Policy overrides the file")]
    [DataRow(null, 1, 0, "Your organisation's WSL policy", DisplayName = "Policy default, and users may not choose")]
    [DataRow("none", 1, 0, "WSL is set to none", DisplayName = "Users can always turn networking off")]
    public void Networking_mode_is_a_problem_unless_wsl_will_really_use_mirrored(string? file, int? policyDefault, int? allowUser, string? expected)
    {
        var problem = ConfigChecks.CheckNetworkingMode(file, policyDefault, allowUser);

        if (expected is null)
        {
            Assert.IsNull(problem);
        }
        else
        {
            Assert.IsNotNull(problem);
            Assert.StartsWith(expected, problem.Title);
            Assert.IsFalse(string.IsNullOrWhiteSpace(problem.Fix));
        }
    }
}
