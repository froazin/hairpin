using WSLProxy.UI.Tray;

namespace WSLProxy.UI.Tests;

/// <summary>The tray glyph's theme choice and the WSL setup snippet.</summary>
[TestClass]
public sealed class TrayTests
{
    [TestMethod]
    [DataRow(false, 0d, 1, Glyph.Black, DisplayName = "Light taskbar")]
    [DataRow(false, 0d, 0, Glyph.White, DisplayName = "Dark taskbar")]
    [DataRow(false, 255d, null, Glyph.White, DisplayName = "Missing value means dark, as Windows treats it")]
    [DataRow(true, 0d, 1, Glyph.White, DisplayName = "High contrast with a dark window ignores the theme")]
    [DataRow(true, 255d, 0, Glyph.Black, DisplayName = "High contrast with a light window ignores the theme")]
    public void Glyph_follows_the_taskbar_theme_or_the_high_contrast_window(bool highContrast, double windowLuma, int? systemUsesLightTheme, Glyph expected)
    {
        Assert.AreEqual(expected, TrayGlyph.Choose(highContrast, windowLuma, systemUsesLightTheme));
    }

    [TestMethod]
    public void Wsl_setup_sets_both_cases_of_each_proxy_variable_to_the_port()
    {
        var lines = TrayApplication.WslSetup(3129).Split('\n');

        CollectionAssert.Contains(lines, "export http_proxy=http://127.0.0.1:3129 https_proxy=http://127.0.0.1:3129");
        CollectionAssert.Contains(lines, "export HTTP_PROXY=http://127.0.0.1:3129 HTTPS_PROXY=http://127.0.0.1:3129");
        CollectionAssert.Contains(lines, "export no_proxy=localhost,127.0.0.1 NO_PROXY=localhost,127.0.0.1");
    }
}
