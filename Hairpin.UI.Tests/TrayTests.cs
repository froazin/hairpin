using Hairpin.UI.Interop;
using Hairpin.UI.Tray;
using Hairpin.UI.Views;

namespace Hairpin.UI.Tests;

/// <summary>The tray glyph's theme choice, the badged icon image, the WSL setup snippet and where the flyout sits.</summary>
[TestClass]
[OSCondition(OperatingSystems.Windows)]
public sealed class TrayTests
{
    [TestMethod]
    [DataRow(false, 0d, 1, Glyph.LightTheme, DisplayName = "Light taskbar")]
    [DataRow(false, 0d, 0, Glyph.DarkTheme, DisplayName = "Dark taskbar")]
    [DataRow(false, 255d, null, Glyph.DarkTheme, DisplayName = "Missing value means dark, as Windows treats it")]
    [DataRow(true, 0d, 1, Glyph.DarkTheme, DisplayName = "High contrast with a dark window ignores the theme")]
    [DataRow(true, 255d, 0, Glyph.LightTheme, DisplayName = "High contrast with a light window ignores the theme")]
    public void Glyph_follows_the_taskbar_theme_or_the_high_contrast_window(bool highContrast, double windowLuma, int? systemUsesLightTheme, Glyph expected)
    {
        Assert.AreEqual(expected, TrayGlyph.Choose(highContrast, windowLuma, systemUsesLightTheme));
    }

    [TestMethod]
    [DataRow(1600, 900, 1588, 888, DisplayName = "Flush with the taskbar and screen edge moves in by the gap")]
    [DataRow(1200, 500, 1200, 500, DisplayName = "Already clear stays put")]
    [DataRow(0, 0, 12, 12, DisplayName = "Flush with a top or left taskbar moves in by the gap")]
    public void Flyout_keeps_a_gap_from_the_edges_of_the_work_area(int left, int top, int expectedLeft, int expectedTop)
    {
        var work = new NativeMethods.RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1400 };
        var window = new NativeMethods.RECT { Left = left, Top = top, Right = left + 320, Bottom = top + 500 };

        Assert.AreEqual((expectedLeft, expectedTop), FlyoutWindow.KeepClear(window, work, gap: 12));
    }

    [TestMethod]
    [DataRow(NativeMethods.ABE_BOTTOM, 48, 0, 0, 2560, 1392, DisplayName = "Bottom")]
    [DataRow(NativeMethods.ABE_TOP, 48, 0, 48, 2560, 1440, DisplayName = "Top")]
    [DataRow(NativeMethods.ABE_LEFT, 60, 60, 0, 2560, 1440, DisplayName = "Left")]
    [DataRow(NativeMethods.ABE_RIGHT, 60, 0, 0, 2500, 1440, DisplayName = "Right")]
    [DataRow(NativeMethods.ABE_BOTTOM, 0, 0, 0, 2560, 1440, DisplayName = "No thickness is ignored")]
    [DataRow(NativeMethods.ABE_BOTTOM, 900, 0, 0, 2560, 1440, DisplayName = "Over half the monitor is ignored")]
    public void Flyout_keeps_clear_of_an_auto_hide_taskbar_the_work_area_leaves_out(uint edge, int thickness, int left, int top, int right, int bottom)
    {
        var monitor = new NativeMethods.RECT { Left = 0, Top = 0, Right = 2560, Bottom = 1440 };

        var work = FlyoutWindow.AvoidAutoHideBars(monitor, monitor, [(edge, thickness)]);

        Assert.AreEqual((left, top, right, bottom), (work.Left, work.Top, work.Right, work.Bottom));
    }

    [TestMethod]
    public void Flyout_taller_than_the_work_area_and_two_gaps_keeps_inside_it_without_them()
    {
        var work = new NativeMethods.RECT { Left = 0, Top = 0, Right = 1920, Bottom = 510 };
        var window = new NativeMethods.RECT { Left = 1600, Top = 10, Right = 1920, Bottom = 510 };

        Assert.AreEqual((1588, 10), FlyoutWindow.KeepClear(window, work, gap: 12));
    }

    [TestMethod]
    public void Wsl_setup_sets_both_cases_of_each_proxy_variable_to_the_port()
    {
        var lines = TrayApplication.WslSetup(3129).Split('\n');

        Assert.Contains("export http_proxy=http://127.0.0.1:3129 https_proxy=http://127.0.0.1:3129", lines);
        Assert.Contains("export HTTP_PROXY=http://127.0.0.1:3129 HTTPS_PROXY=http://127.0.0.1:3129", lines);
        Assert.Contains("export no_proxy=localhost,127.0.0.1 NO_PROXY=localhost,127.0.0.1", lines);
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
