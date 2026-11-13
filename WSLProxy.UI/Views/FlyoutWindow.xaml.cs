using System.Globalization;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using WSLProxy.Relay.Status;
using static WSLProxy.UI.Interop.NativeMethods;

namespace WSLProxy.UI.Views;

/// <summary>
/// The tray flyout: the relay's status and the main actions. It opens next to the tray icon and hides as soon as it loses
/// focus, like the flyouts Windows 11 opens from the notification area.
/// </summary>
public partial class FlyoutWindow : Window
{
    private readonly DispatcherTimer _copiedTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private DateTime _hiddenAt = DateTime.MinValue;
    private Rect? _anchor;
    private RelayState _state;

    /// <summary>Creates the flyout. It starts hidden.</summary>
    public FlyoutWindow()
    {
        InitializeComponent();
        Deactivated += (_, _) => HideFlyout();

        // The height follows the content (an error can appear while it's open), so re-place it to stay clear of the taskbar.
        SizeChanged += (_, _) =>
        {
            if (IsVisible)
            {
                Place();
            }
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape)
            {
                HideFlyout();
            }
        };
        _copiedTimer.Tick += (_, _) =>
        {
            _copiedTimer.Stop();
            CopyText.Text = "Copy WSL setup";
        };
    }

    /// <summary>Raised when the user asks to pause or resume.</summary>
    public event EventHandler<bool>? PauseRequested;

    /// <summary>Raised when the user asks to copy the WSL setup.</summary>
    public event EventHandler? CopyRequested;

    /// <summary>Raised when the user asks for the settings window.</summary>
    public event EventHandler? SettingsRequested;

    /// <summary>Raised when the user asks to open the log folder.</summary>
    public event EventHandler? LogsRequested;

    /// <summary>Raised when the user asks to quit.</summary>
    public event EventHandler? QuitRequested;

    /// <summary>Shows the flyout next to <paramref name="anchor"/> (physical pixels), or hides it if it's already open.</summary>
    public void Toggle(Rect anchor)
    {
        // Clicking the icon while the flyout is open first deactivates it, which hides it. Don't reopen it straight away.
        if (IsVisible || (DateTime.UtcNow - _hiddenAt).TotalMilliseconds < 300)
        {
            HideFlyout();
            return;
        }

        Open(anchor);
    }

    /// <summary>Shows the flyout next to <paramref name="anchor"/> (physical pixels), and keeps it open if it already is.</summary>
    public void Open(Rect anchor)
    {
        _anchor = anchor;
        var helper = new WindowInteropHelper(this);
        var hwnd = helper.EnsureHandle();

        // Move the hidden window onto the icon's monitor before it's shown and measured, so a per-monitor DPI change
        // happens now rather than after it's placed.
        SetWindowPos(hwnd, HWND_TOPMOST, (int)anchor.Left, (int)anchor.Top, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);
        var corner = DWMWCP_ROUND;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
        Show();
        Place();

        // A tray-launched window must take the foreground, or it won't get Deactivated when the user clicks elsewhere.
        SetForegroundWindow(hwnd);
        Activate();
    }

    /// <summary>Places the flyout beside the anchor, inside the work area and clear of the icon, in physical pixels.</summary>
    private void Place()
    {
        if (_anchor is not { } anchor)
        {
            return;
        }

        var hwnd = new WindowInteropHelper(this).Handle;
        GetWindowRect(hwnd, out var current);
        var size = new SIZE { Cx = current.Right - current.Left, Cy = current.Bottom - current.Top };
        var point = new POINT { X = (int)(anchor.Left + (anchor.Width / 2)), Y = (int)anchor.Top };
        var exclude = new RECT { Left = (int)anchor.Left, Top = (int)anchor.Top, Right = (int)anchor.Right, Bottom = (int)anchor.Bottom };
        if (CalculatePopupWindowPosition(ref point, ref size, TPM_RIGHTALIGN | TPM_BOTTOMALIGN | TPM_VERTICAL | TPM_WORKAREA, ref exclude, out var placed))
        {
            SetWindowPos(hwnd, HWND_TOPMOST, placed.Left, placed.Top, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);
        }
    }

    /// <summary>Shows a snapshot of the relay's state.</summary>
    public void Update(RelaySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _state = snapshot.State;
        var (label, brushKey) = snapshot.State switch
        {
            RelayState.Running => ("Running", "SystemFillColorSuccessBrush"),
            RelayState.Paused => ("Paused", "SystemFillColorCautionBrush"),
            RelayState.Faulted => ("Needs attention", "SystemFillColorCriticalBrush"),
            RelayState.Stopped => ("Stopped", "SystemFillColorNeutralBrush"),
            _ => ("Starting", "SystemFillColorNeutralBrush"),
        };
        StateText.Text = label;
        StateDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, brushKey);
        StateBadge.SetResourceReference(BackgroundProperty, brushKey.Replace("Brush", "BackgroundBrush", StringComparison.Ordinal));

        EndpointText.Text = snapshot.ListenEndpoint;
        UpstreamText.Text = snapshot.Upstream is null
            ? "None: connecting directly"
            : snapshot.Upstream + " · " + snapshot.UpstreamHealth switch
            {
                UpstreamHealth.Reachable => "reachable",
                UpstreamHealth.Unreachable => "unreachable",
                _ => "checking",
            };
        TrafficText.Text = string.Format(CultureInfo.CurrentCulture, "{0:N0} open · {1:N0} requests", snapshot.ActiveConnections, snapshot.TotalRequests);

        ErrorPanel.Visibility = string.IsNullOrEmpty(snapshot.LastError) ? Visibility.Collapsed : Visibility.Visible;
        ErrorText.Text = snapshot.LastError ?? string.Empty;

        var paused = snapshot.State == RelayState.Paused;
        PauseText.Text = paused ? "Resume" : "Pause";
        PauseGlyph.Text = ((char)(paused ? 0xE768 : 0xE769)).ToString(); // Segoe Fluent Icons: Play, Pause.
    }

    /// <summary>Confirms on the button that the setup was copied.</summary>
    public void ShowCopied()
    {
        CopyText.Text = "Copied";
        _copiedTimer.Stop();
        _copiedTimer.Start();
    }

    private void HideFlyout()
    {
        if (IsVisible)
        {
            Hide();
            _hiddenAt = DateTime.UtcNow;
        }
    }

    private void OnPauseClick(object sender, RoutedEventArgs e) => PauseRequested?.Invoke(this, _state != RelayState.Paused);

    private void OnCopyClick(object sender, RoutedEventArgs e) => CopyRequested?.Invoke(this, EventArgs.Empty);

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        HideFlyout();
        SettingsRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnLogsClick(object sender, RoutedEventArgs e)
    {
        HideFlyout();
        LogsRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnQuitClick(object sender, RoutedEventArgs e) => QuitRequested?.Invoke(this, EventArgs.Empty);
}
