using System.Globalization;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using WSLProxy.Relay.Status;
using WSLProxy.UI.Diagnostics;
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
    private TrafficSummary? _traffic;

    /// <summary>How many points the chart shows: two minutes at one a second.</summary>
    public const int ChartPoints = 120;

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

    /// <summary>Raised when the user asks for the log window.</summary>
    public event EventHandler? LogsRequested;

    /// <summary>Raised when the user turns debug logging on (true) or off (false).</summary>
    public event EventHandler<bool>? DebugLoggingRequested;

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

        ErrorPanel.Visibility = string.IsNullOrEmpty(snapshot.LastError) ? Visibility.Collapsed : Visibility.Visible;
        ErrorText.Text = snapshot.LastError ?? string.Empty;

        var paused = snapshot.State == RelayState.Paused;
        PauseText.Text = paused ? "Resume" : "Pause";
        PauseGlyph.Text = ((char)(paused ? 0xE768 : 0xE769)).ToString(); // Segoe Fluent Icons: Play, Pause.
    }

    /// <summary>Shows the traffic rates, chart and totals.</summary>
    public void UpdateTraffic(TrafficSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        _traffic = summary;
        DownRateText.Text = ByteFormat.Rate(summary.DownPerSecond);
        UpRateText.Text = ByteFormat.Rate(summary.UpPerSecond);
        RequestsText.Text = summary.RequestsLastMinute.ToString("N0", CultureInfo.CurrentCulture);
        var latest = summary.Latest;
        OpenText.Text = (latest?.ActiveConnections ?? 0).ToString("N0", CultureInfo.CurrentCulture);
        ErrorsText.Text = (latest?.Errors ?? 0).ToString("N0", CultureInfo.CurrentCulture);
        var routed = (latest?.DirectRequests ?? 0) + (latest?.UpstreamRequests ?? 0);
        TotalsText.Text = latest is null
            ? "No traffic yet."
            : string.Format(CultureInfo.CurrentCulture, "Since start: {0} down, {1} up, {2:N0} requests{3}.",
                ByteFormat.Size(latest.BytesDown), ByteFormat.Size(latest.BytesUp), latest.Requests,
                routed == 0 ? string.Empty : string.Format(CultureInfo.CurrentCulture, ", {0:P0} direct", (double)latest.DirectRequests / routed));
        DrawChart();
    }

    /// <summary>Shows the settings that stop mirrored mode, or hides the list when there are none.</summary>
    public void SetProblems(IReadOnlyList<ConfigProblem> problems)
    {
        ArgumentNullException.ThrowIfNull(problems);
        ProblemsList.ItemsSource = problems;
        ProblemsList.Visibility = problems.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Shows whether debug logging is on.</summary>
    public void SetDebugLogging(bool enabled) => DebugBox.IsChecked = enabled;

    /// <summary>Confirms on the button that the setup was copied.</summary>
    public void ShowCopied()
    {
        CopyText.Text = "Copied";
        _copiedTimer.Stop();
        _copiedTimer.Start();
    }

    private void OnChartSizeChanged(object sender, SizeChangedEventArgs e) => DrawChart();

    /// <summary>Draws the chart right-aligned, newest point at the right edge, scaled to the busiest point shown.</summary>
    private void DrawChart()
    {
        var width = Chart.ActualWidth;
        var height = Chart.ActualHeight;
        var down = _traffic?.Down ?? [];
        var up = _traffic?.Up ?? [];
        DownLine.Points = [];
        UpLine.Points = [];
        DownArea.Points = [];
        if (width <= 0 || height <= 0 || down.Count < 2)
        {
            return;
        }

        // At least 1 KB/s full scale, so an idle relay draws a flat line rather than noise.
        var top = Math.Max(1024, Math.Max(down.Max(), up.Max())) * 1.1;
        var step = width / (ChartPoints - 1);
        PointCollection Line(IReadOnlyList<double> values)
        {
            var points = new PointCollection(values.Count);
            for (var i = 0; i < values.Count; i++)
            {
                points.Add(new Point(width - ((values.Count - 1 - i) * step), height - (values[i] / top * height)));
            }

            return points;
        }

        var downPoints = Line(down);
        DownLine.Points = downPoints;
        UpLine.Points = Line(up);
        var area = new PointCollection(downPoints) { new Point(width, height), new Point(downPoints[0].X, height) };
        DownArea.Points = area;
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

    private void OnDebugClick(object sender, RoutedEventArgs e) => DebugLoggingRequested?.Invoke(this, DebugBox.IsChecked != true);

    private void OnQuitClick(object sender, RoutedEventArgs e) => QuitRequested?.Invoke(this, EventArgs.Empty);
}
