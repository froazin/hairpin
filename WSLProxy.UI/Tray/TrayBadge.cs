using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using static WSLProxy.UI.Interop.NativeMethods;

namespace WSLProxy.UI.Tray;

/// <summary>Draws the small red X that says a setting needs fixing onto the tray glyph.</summary>
internal static class TrayBadge
{
    // Windows' own critical red, the colour of its error badges. Tray icons are images, so a fixed colour is right here.
    private static readonly SolidColorBrush Red = Freeze(new SolidColorBrush(Color.FromRgb(0xC4, 0x2B, 0x1C)));

    /// <summary>
    /// Returns a new icon: <paramref name="icon"/> with a red circle and a white X in its lower-right corner, and a
    /// transparent gap around the circle so it stands out on any taskbar. The caller destroys both icons.
    /// </summary>
    public static unsafe IntPtr Compose(IntPtr icon, int size)
    {
        var glyph = Imaging.CreateBitmapSourceFromHIcon(icon, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(size, size));
        var radius = Math.Round(size * 0.23);
        var centre = new Point(size - radius, size - radius);
        var whole = new Rect(0, 0, size, size);

        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            var gap = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(whole),
                new EllipseGeometry(centre, radius + Math.Max(1, size / 16.0), radius + Math.Max(1, size / 16.0)));
            context.PushClip(gap);
            context.DrawImage(glyph, whole);
            context.Pop();

            context.DrawEllipse(Red, null, centre, radius, radius);
            var arm = radius * 0.45;
            var pen = new Pen(Brushes.White, Math.Max(1.25, size / 12.0)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            context.DrawLine(pen, new Point(centre.X - arm, centre.Y - arm), new Point(centre.X + arm, centre.Y + arm));
            context.DrawLine(pen, new Point(centre.X + arm, centre.Y - arm), new Point(centre.X - arm, centre.Y + arm));
        }

        var rendered = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        rendered.Render(visual);
        var straight = new FormatConvertedBitmap(rendered, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[size * size * 4];
        straight.CopyPixels(pixels, size * 4, 0);

        var image = IconFile.FromBgra(pixels, size);
        fixed (byte* bits = image)
        {
            var badged = CreateIconFromResourceEx(bits, (uint)image.Length, true, 0x00030000, size, size, 0);
            return badged != IntPtr.Zero ? badged : throw new InvalidOperationException("Windows couldn't create the tray icon.");
        }
    }

    private static SolidColorBrush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }
}
