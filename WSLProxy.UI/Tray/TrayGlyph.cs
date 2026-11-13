using Microsoft.Win32;

namespace WSLProxy.UI.Tray;

/// <summary>Which tray glyph to show.</summary>
public enum Glyph
{
    /// <summary>The dark glyph, for a light taskbar.</summary>
    LightTheme,

    /// <summary>The light glyph, for a dark taskbar.</summary>
    DarkTheme,
}

/// <summary>Picks the tray glyph from the taskbar's theme. Pure apart from <see cref="ReadSystemUsesLightTheme"/>.</summary>
public static class TrayGlyph
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    /// <summary>
    /// Chooses the glyph. The taskbar follows <c>SystemUsesLightTheme</c>, not the apps' theme, so a "Custom" Windows theme with a
    /// dark taskbar and light apps still gets the dark theme glyph. A missing value means dark, as Windows treats it. Under high
    /// contrast the theme values don't describe the taskbar, so the glyph contrasts with the high-contrast window colour instead.
    /// </summary>
    /// <param name="highContrast">True when high contrast is on.</param>
    /// <param name="windowLuma">The high-contrast window colour's luma, 0 to 255.</param>
    /// <param name="systemUsesLightTheme">The <c>SystemUsesLightTheme</c> value, or null when it's missing.</param>
    public static Glyph Choose(bool highContrast, double windowLuma, int? systemUsesLightTheme) =>
        highContrast
            ? (windowLuma < 128 ? Glyph.DarkTheme : Glyph.LightTheme)
            : ((systemUsesLightTheme ?? 0) != 0 ? Glyph.LightTheme : Glyph.DarkTheme);

    /// <summary>Reads <c>SystemUsesLightTheme</c> for the current user, or null when it's missing.</summary>
    public static int? ReadSystemUsesLightTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
        return key?.GetValue("SystemUsesLightTheme") as int?;
    }

    /// <summary>Relative luma (ITU-R BT.601) of a colour, 0 to 255.</summary>
    public static double Luma(byte r, byte g, byte b) => (0.299 * r) + (0.587 * g) + (0.114 * b);
}
