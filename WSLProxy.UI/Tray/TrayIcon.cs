using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using WSLProxy.UI.Interop;
using static WSLProxy.UI.Interop.NativeMethods;

namespace WSLProxy.UI.Tray;

/// <summary>
/// A notification area icon built directly on <c>Shell_NotifyIcon</c> (version 4). It keeps the right glyph for the taskbar's
/// theme and DPI, comes back after Explorer restarts, and reports clicks with the icon's screen rectangle so a flyout can be
/// anchored to it. Create and use it on the UI thread.
/// </summary>
/// <remarks>
/// The icon is identified by window handle and ID rather than a GUID: a GUID registration is tied to the exe's path, which
/// breaks development builds and upgrades that move the exe.
/// </remarks>
public sealed class TrayIcon : IDisposable
{
    private const uint IconId = 1;
    private const int CallbackMessage = WM_APP + 1;

    private readonly HwndSource _window;
    private readonly uint _taskbarCreatedMessage;
    private readonly Dictionary<Glyph, byte[]> _iconFiles;
    private IntPtr _icon;
    private (Glyph Glyph, int Size, bool Badge) _current;
    private bool _badge;
    private string _tooltip = "WSL Proxy Relay";
    private bool _added;
    private bool _disposed;
    private bool _ignoreNextSelect;
    private readonly IntPtr _handle;

    /// <summary>Creates the icon and adds it to the notification area.</summary>
    public TrayIcon()
    {
        _iconFiles = new()
        {
            [Glyph.Black] = LoadResource("WSLProxy.UI.Tray.Black.ico"),
            [Glyph.White] = LoadResource("WSLProxy.UI.Tray.White.ico"),
        };

        // A hidden top-level window: message-only windows don't get the TaskbarCreated or settings-change broadcasts.
        _window = new HwndSource(new HwndSourceParameters("WSLProxyRelayTray") { WindowStyle = unchecked((int)0x80000000), Width = 0, Height = 0 });
        _handle = _window.Handle;
        _window.AddHook(WndProc);
        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
        Refresh(force: true);
    }

    /// <summary>Raised when the user clicks the icon or presses Enter on it, with the icon's rectangle in physical pixels.</summary>
    public event EventHandler<Rect>? Activated;

    /// <summary>Raised when the user double-clicks the icon, with the icon's rectangle in physical pixels.</summary>
    public event EventHandler<Rect>? DoubleClicked;

    /// <summary>The window that owns the icon, for positioning and foreground handling.</summary>
    public IntPtr Handle => _handle; // Kept from construction: WPF may destroy the window before Dispose runs.

    /// <summary>Sets the tooltip. Windows limits it to 127 characters.</summary>
    public void SetTooltip(string text)
    {
        _tooltip = text.Length > 127 ? text[..127] : text;
        if (_added)
        {
            Notify(NIM_MODIFY, NIF_TIP | NIF_SHOWTIP);
        }
    }

    /// <summary>Shows or hides a small red X on the icon, which says a setting needs fixing.</summary>
    public void SetBadge(bool show)
    {
        _badge = show;
        if (!_disposed)
        {
            Refresh(force: false);
        }
    }

    /// <summary>Gets the icon's rectangle in physical pixels, or null when the shell can't say.</summary>
    public Rect? GetIconRect()
    {
        var id = new NOTIFYICONIDENTIFIER { cbSize = (uint)Marshal.SizeOf<NOTIFYICONIDENTIFIER>(), hWnd = Handle, uID = IconId };
        return Shell_NotifyIconGetRect(ref id, out var rect) == 0
            ? new Rect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top)
            : null;
    }

    /// <summary>Removes the icon and frees its resources.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_added)
        {
            Notify(NIM_DELETE, 0);
        }

        if (_icon != IntPtr.Zero)
        {
            DestroyIcon(_icon);
        }

        _window.RemoveHook(WndProc);
        _window.Dispose();
    }

    /// <summary>Picks the glyph and size again, and updates or re-adds the icon when either changed.</summary>
    private void Refresh(bool force)
    {
        var luma = TrayGlyph.Luma(SystemColors.WindowColor.R, SystemColors.WindowColor.G, SystemColors.WindowColor.B);
        var glyph = TrayGlyph.Choose(SystemParameters.HighContrast, luma, TrayGlyph.ReadSystemUsesLightTheme());
        var size = SmallIconSize();
        if (!force && _added && (glyph, size, _badge) == _current)
        {
            return;
        }

        var icon = CreateIcon(_iconFiles[glyph], size);
        if (_badge)
        {
            var plain = icon;
            try
            {
                icon = TrayBadge.Compose(plain, size);
            }
            finally
            {
                DestroyIcon(plain);
            }
        }

        var old = _icon;
        _icon = icon;
        _current = (glyph, size, _badge);
        if (force || !_added)
        {
            // If the shell still has the icon (TaskbarCreated also fires on a DPI change), NIM_ADD fails: update it instead.
            _added = Notify(NIM_ADD, NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP)
                || Notify(NIM_MODIFY, NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP);
            if (_added)
            {
                var data = NewData(0);
                data.uVersion = NOTIFYICON_VERSION_4;
                Shell_NotifyIcon(NIM_SETVERSION, ref data);
            }
        }
        else
        {
            Notify(NIM_MODIFY, NIF_ICON | NIF_TIP | NIF_SHOWTIP);
        }

        if (old != IntPtr.Zero)
        {
            DestroyIcon(old);
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == CallbackMessage)
        {
            // Version 4: the low word of lParam is the event.
            var evt = unchecked((int)(lParam.ToInt64() & 0xFFFF));
            var rect = GetIconRect() ?? new Rect(LowWord(wParam), HighWord(wParam), 1, 1);
            if (evt == WM_LBUTTONDBLCLK)
            {
                // A double-click also sends NIN_SELECT for its second release. Open the flyout once and skip that select.
                _ignoreNextSelect = true;
                DoubleClicked?.Invoke(this, rect);
                handled = true;
            }
            else if (evt == NIN_SELECT && _ignoreNextSelect)
            {
                _ignoreNextSelect = false;
                handled = true;
            }
            else if (evt is NIN_SELECT or NIN_KEYSELECT or WM_CONTEXTMENU)
            {
                Activated?.Invoke(this, rect);
                handled = true;
            }
        }
        else if (_taskbarCreatedMessage != 0 && msg == (int)_taskbarCreatedMessage)
        {
            // Explorer restarted, or the primary display's DPI changed: every icon is gone.
            _added = false;
            Refresh(force: true);
        }
        else if (msg is WM_SETTINGCHANGE or WM_SYSCOLORCHANGE or WM_DISPLAYCHANGE or WM_DPICHANGED)
        {
            // Includes the "ImmersiveColorSet" broadcast sent when the light or dark theme changes.
            // Let WPF refresh its cached system colours and high-contrast flag first.
            _window.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () => { if (!_disposed) { Refresh(force: false); } });
        }

        return IntPtr.Zero;
    }

    private bool Notify(uint message, uint flags)
    {
        var data = NewData(flags);
        return Shell_NotifyIcon(message, ref data);
    }

    private unsafe NOTIFYICONDATAW NewData(uint flags)
    {
        var data = new NOTIFYICONDATAW
        {
            cbSize = (uint)sizeof(NOTIFYICONDATAW),
            hWnd = Handle,
            uID = IconId,
            uFlags = flags,
            uCallbackMessage = CallbackMessage,
            hIcon = _icon,
        };
        var tip = _tooltip.AsSpan();
        for (var i = 0; i < tip.Length && i < 127; i++)
        {
            data.szTip[i] = tip[i];
        }

        return data;
    }

    /// <summary>The small-icon size at the taskbar's DPI: 16 px at 100%, 20 at 125%, 24 at 150%, 32 at 200%.</summary>
    private static int SmallIconSize()
    {
        var taskbar = FindWindow("Shell_TrayWnd", null);
        var dpi = taskbar != IntPtr.Zero ? GetDpiForWindow(taskbar) : 0;
        if (dpi == 0)
        {
            dpi = GetDpiForSystem();
        }

        return Math.Max(16, GetSystemMetricsForDpi(SM_CXSMICON, dpi));
    }

    /// <summary>Creates an icon of the requested size from an .ico file, choosing the exact image or the next larger one.</summary>
    private static unsafe IntPtr CreateIcon(byte[] iconFile, int size)
    {
        var (offset, length) = IconFile.SelectImage(iconFile, size);
        fixed (byte* bits = &iconFile[offset])
        {
            var icon = CreateIconFromResourceEx(bits, (uint)length, true, 0x00030000, size, size, 0);
            return icon != IntPtr.Zero ? icon : throw new InvalidOperationException("Windows couldn't create the tray icon.");
        }
    }

    private static byte[] LoadResource(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("Missing embedded resource " + name + ".");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static int LowWord(IntPtr value) => unchecked((short)(value.ToInt64() & 0xFFFF));

    private static int HighWord(IntPtr value) => unchecked((short)((value.ToInt64() >> 16) & 0xFFFF));
}

/// <summary>Reads the image directory of an .ico file. Pure.</summary>
public static class IconFile
{
    /// <summary>
    /// Returns the byte range of the image to use for <paramref name="size"/>: an exact match, otherwise the smallest larger image
    /// (Windows scales down more cleanly than up), otherwise the largest.
    /// </summary>
    public static (int Offset, int Length) SelectImage(ReadOnlySpan<byte> file, int size)
    {
        if (file.Length < 6 || BitConverter.ToUInt16(file[2..4]) != 1)
        {
            throw new InvalidDataException("Not an icon file.");
        }

        var count = BitConverter.ToUInt16(file[4..6]);
        (int Width, int Offset, int Length)? best = null;
        for (var i = 0; i < count; i++)
        {
            var entry = file.Slice(6 + (16 * i), 16);
            var width = entry[0] == 0 ? 256 : entry[0];
            var length = (int)BitConverter.ToUInt32(entry[8..12]);
            var offset = (int)BitConverter.ToUInt32(entry[12..16]);
            if (offset < 0 || length <= 0 || offset + length > file.Length)
            {
                continue;
            }

            var better = best is null
                || (width >= size && (best.Value.Width < size || width < best.Value.Width))
                || (width < size && best.Value.Width < size && width > best.Value.Width);
            if (better)
            {
                best = (width, offset, length);
            }
        }

        return best is { } b ? (b.Offset, b.Length) : throw new InvalidDataException("The icon file has no usable image.");
    }

    /// <summary>
    /// Builds an icon image, as <c>CreateIconFromResourceEx</c> reads it, from square 32-bit BGRA pixels with straight alpha,
    /// top row first: a <c>BITMAPINFOHEADER</c> with double the height, the pixels bottom row first, then an empty AND mask,
    /// because the alpha channel does its job.
    /// </summary>
    public static byte[] FromBgra(ReadOnlySpan<byte> pixels, int size)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 1);
        var stride = size * 4;
        if (pixels.Length != stride * size)
        {
            throw new ArgumentException("Expected " + (stride * size) + " bytes of pixels.", nameof(pixels));
        }

        const int headerSize = 40;
        var maskStride = ((size + 31) / 32) * 4;
        var image = new byte[headerSize + pixels.Length + (maskStride * size)];
        var header = image.AsSpan(0, headerSize);
        BitConverter.TryWriteBytes(header[0..4], headerSize);
        BitConverter.TryWriteBytes(header[4..8], size);
        BitConverter.TryWriteBytes(header[8..12], size * 2);
        BitConverter.TryWriteBytes(header[12..14], (ushort)1);
        BitConverter.TryWriteBytes(header[14..16], (ushort)32);
        BitConverter.TryWriteBytes(header[20..24], pixels.Length + (maskStride * size));
        for (var row = 0; row < size; row++)
        {
            pixels.Slice(row * stride, stride).CopyTo(image.AsSpan(headerSize + ((size - 1 - row) * stride), stride));
        }

        return image;
    }
}
