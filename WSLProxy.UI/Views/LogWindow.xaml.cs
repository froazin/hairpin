using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using WSLProxy.UI.Diagnostics;

namespace WSLProxy.UI.Views;

/// <summary>
/// Shows the recent log lines held in memory, live. It redraws at most four times a second however fast lines arrive,
/// and stops listening for new lines when it's closed.
/// </summary>
public partial class LogWindow : Window
{
    private static readonly LogLevel[] Minimums = [LogLevel.Trace, LogLevel.Information, LogLevel.Warning, LogLevel.Error];

    private readonly LogStore _store;
    private readonly DispatcherTimer _refreshTimer;
    private IReadOnlyList<LogRow> _rows = [];
    private int _dirty;

    /// <summary>Creates the window for <paramref name="store"/>. <paramref name="openFolder"/> opens the log folder.</summary>
    public LogWindow(LogStore store, Action openFolder)
    {
        ArgumentNullException.ThrowIfNull(store);
        InitializeComponent();
        _store = store;
        OpenFolder = openFolder;
        _refreshTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, OnRefreshTick, Dispatcher);
        _store.Changed += OnStoreChanged;
        Closed += (_, _) =>
        {
            _store.Changed -= OnStoreChanged;
            _refreshTimer.Stop();
        };
        Refresh();
    }

    private Action OpenFolder { get; }

    // Raised on whichever thread logged. Only mark the view as stale here: never touch WPF objects, and never log.
    private void OnStoreChanged(object? sender, EventArgs e)
    {
        if (Interlocked.Exchange(ref _dirty, 1) == 0)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Background, _refreshTimer.Start);
        }
    }

    private void OnRefreshTick(object? sender, EventArgs e)
    {
        _refreshTimer.Stop();
        if (FollowBox.IsChecked == true)
        {
            Refresh();
            return;
        }

        // Swapping the rows would jump the list back to the top while the user reads, so hold new lines back.
        Interlocked.Exchange(ref _dirty, 0);
        SummaryText.Text = string.Format(CultureInfo.CurrentCulture,
            "Showing {0:N0} lines. New lines are held back until you turn Follow new lines on again.", _rows.Count);
    }

    private void Refresh()
    {
        Interlocked.Exchange(ref _dirty, 0);
        var minimum = Minimums[Math.Clamp(LevelBox.SelectedIndex, 0, Minimums.Length - 1)];
        _rows = LogView.Filter(_store.Snapshot(), minimum, FilterBox.Text);
        EntriesList.ItemsSource = _rows;
        SummaryText.Text = string.Format(CultureInfo.CurrentCulture,
            "Showing {0:N0} lines. The window keeps the latest {1:N0} in memory, in local time; the log files keep everything, in UTC.",
            _rows.Count, _store.Capacity);
        if (FollowBox.IsChecked == true && _rows.Count > 0)
        {
            EntriesList.ScrollIntoView(_rows[^1]);
        }
    }

    private void OnFilterChanged(object sender, RoutedEventArgs e)
    {
        // SelectionChanged fires while InitializeComponent is still building the window.
        if (EntriesList is not null)
        {
            Refresh();
        }
    }

    private void OnFollowChanged(object sender, RoutedEventArgs e) => Refresh();

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(string.Join(Environment.NewLine, _rows));
            CopyButton.Content = "Copied";
            var reset = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            reset.Tick += (_, _) =>
            {
                reset.Stop();
                CopyButton.Content = "Copy";
            };
            reset.Start();
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // Another app is holding the clipboard. The user can click again.
        }
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        _store.Clear();
        Refresh();
    }

    private void OnOpenFolderClick(object sender, RoutedEventArgs e) => OpenFolder();
}
