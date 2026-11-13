using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using WSLProxy.UI.Settings;

namespace WSLProxy.UI.Views;

/// <summary>The settings window: upstream proxies, bypass list, relay port and start at sign-in.</summary>
public partial class SettingsWindow : Window
{
    private readonly ObservableCollection<string> _entries;
    private readonly Func<UserSettings, IReadOnlyList<string>> _save;
    private readonly bool _startAtLogin;

    /// <summary>Creates the window showing <paramref name="current"/>. <paramref name="save"/> validates and saves, returning any problems.</summary>
    public SettingsWindow(UserSettings current, Func<UserSettings, IReadOnlyList<string>> save)
    {
        ArgumentNullException.ThrowIfNull(current);
        InitializeComponent();
        _save = save;
        _startAtLogin = current.StartAtLogin;
        _entries = [.. current.NoProxy];
        EntriesList.ItemsSource = _entries;
        HttpHostBox.Text = current.HttpProxyHost;
        HttpPortBox.Text = current.HttpProxyPort.ToString(CultureInfo.InvariantCulture);
        HttpsHostBox.Text = current.HttpsProxyHost;
        HttpsPortBox.Text = current.HttpsProxyPort?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        ListenPortBox.Text = current.ListenPort.ToString(CultureInfo.InvariantCulture);
        DenyLocalBox.IsChecked = current.DenyLocalDestinations;
        StartAtLoginBox.IsChecked = current.StartAtLogin;
    }

    /// <summary>Raised when the user asks for the log window.</summary>
    public event EventHandler? LogsRequested;

    /// <summary>Raised when the user turns debug logging on (true) or off (false). It applies straight away, without Save.</summary>
    public event EventHandler<bool>? DebugLoggingRequested;

    /// <summary>
    /// Hides the start-at-sign-in box and says where Windows manages it instead, for the app installed from its MSIX
    /// package. The app can't read the package's startup task without WinRT, so it doesn't show a box it can't fill in.
    /// The box is also disabled, so saving keeps the stored value.
    /// </summary>
    public void ShowStartupManagedByWindows()
    {
        StartAtLoginBox.IsEnabled = false;
        StartAtLoginBox.Visibility = Visibility.Collapsed;
        StartupHint.Visibility = Visibility.Visible;
    }

    /// <summary>Shows whether debug logging is on.</summary>
    public void SetDebugLogging(bool enabled) => DebugBox.IsChecked = enabled;

    private void OnLogsClick(object sender, RoutedEventArgs e) => LogsRequested?.Invoke(this, EventArgs.Empty);

    private void OnDebugClick(object sender, RoutedEventArgs e) => DebugLoggingRequested?.Invoke(this, DebugBox.IsChecked == true);

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();

    /// <inheritdoc />
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
            return;
        }

        base.OnPreviewKeyDown(e);
    }

    private void OnAddClick(object sender, RoutedEventArgs e) => AddEntries();

    private void OnNewEntryKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            AddEntries();
            e.Handled = true;
        }
    }

    /// <summary>Adds one entry, or several pasted as a no_proxy value separated by commas, semicolons or spaces.</summary>
    private void AddEntries()
    {
        var input = BypassEntries.Read(NewEntryBox.Text, _entries);
        foreach (var entry in input.Added)
        {
            _entries.Add(entry);
        }

        // Keep anything that didn't parse in the box, so the user can fix it.
        NewEntryBox.Text = string.Join(", ", input.Rejected);
        EntryProblemText.Text = string.Join(Environment.NewLine, input.Problems);
        EntryProblemText.Visibility = input.Problems.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        NewEntryBox.Focus();
    }

    private void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        foreach (var item in EntriesList.SelectedItems.Cast<string>().ToList())
        {
            _entries.Remove(item);
        }
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        // The HTTPS port may be left empty, to use the HTTP one.
        var httpsPortEmpty = string.IsNullOrWhiteSpace(HttpsPortBox.Text);
        if (!TryParsePort(HttpPortBox.Text, out var httpPort) || !TryParsePort(ListenPortBox.Text, out var listenPort)
            || (!TryParsePort(HttpsPortBox.Text, out var httpsPort) && !httpsPortEmpty))
        {
            ProblemText.Text = "Ports must be numbers from 1 to 65535.";
            return;
        }

        var settings = new UserSettings(
            listenPort,
            HttpHostBox.Text.Trim(),
            httpPort,
            HttpsHostBox.Text.Trim(),
            httpsPortEmpty ? null : httpsPort,
            [.. _entries],
            DenyLocalBox.IsChecked == true,
            StartAtLoginBox.IsEnabled ? StartAtLoginBox.IsChecked == true : _startAtLogin);
        var problems = _save(settings);
        if (problems.Count > 0)
        {
            ProblemText.Text = string.Join(" ", problems);
            return;
        }

        // Shown with Show(), not ShowDialog(), so the tray keeps running: close it directly.
        Close();
    }

    private static bool TryParsePort(string text, out int port) =>
        int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out port) && port is >= 1 and <= 65535;
}
