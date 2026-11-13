using WSLProxy.UI.Tray;

namespace WSLProxy.UI.Tests;

/// <summary>The tray glyph's theme choice, the badged icon image and the WSL setup snippet.</summary>
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

    [TestMethod]
    public void Icon_image_from_pixels_has_a_double_height_header_and_rows_bottom_first()
    {
        byte[] pixels = [1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4];

        var image = IconFile.FromBgra(pixels, size: 2);

        Assert.HasCount(40 + 16 + 8, image);
        Assert.AreEqual(2, BitConverter.ToInt32(image, 4));
        Assert.AreEqual(4, BitConverter.ToInt32(image, 8));
        Assert.AreEqual(32, BitConverter.ToUInt16(image, 14));
        Assert.AreEqual("3,3,3,3,4,4,4,4,1,1,1,1,2,2,2,2", string.Join(",", image[40..56]));
        Assert.IsTrue(image[56..].All(b => b == 0));
    }
}
