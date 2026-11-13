using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using WSLProxy.Relay.Routing;
using WSLProxy.UI.Settings;

namespace WSLProxy.UI.Views;

/// <summary>The settings window: upstream proxy, bypass list, relay port and start at sign-in.</summary>
public partial class SettingsWindow : Window
{
    private readonly ObservableCollection<string> _entries;
    private readonly Func<UserSettings, IReadOnlyList<string>> _save;

    /// <summary>Creates the window showing <paramref name="current"/>. <paramref name="save"/> validates and saves, returning any problems.</summary>
    public SettingsWindow(UserSettings current, Func<UserSettings, IReadOnlyList<string>> save)
    {
        ArgumentNullException.ThrowIfNull(current);
        InitializeComponent();
        _save = save;
        _entries = [.. current.NoProxy];
        EntriesList.ItemsSource = _entries;
        UpstreamHostBox.Text = current.UpstreamHost;
        UpstreamPortBox.Text = current.UpstreamPort.ToString(CultureInfo.InvariantCulture);
        ListenPortBox.Text = current.ListenPort.ToString(CultureInfo.InvariantCulture);
        DenyLocalBox.IsChecked = current.DenyLocalDestinations;
        StartAtLoginBox.IsChecked = current.StartAtLogin;
    }

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
        var problems = new List<string>();
        var parts = NewEntryBox.Text.Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var rejected = new List<string>();
        foreach (var part in parts)
        {
            if (!NoProxyRule.TryParse(part, out var rule, out var error))
            {
                problems.Add(part + ": " + error);
                rejected.Add(part);
                continue;
            }

            if (!_entries.Any(e => NoProxyRule.TryParse(e, out var existing, out _) && existing.Normalised == rule.Normalised))
            {
                _entries.Add(rule.Normalised);
            }
        }

        // Keep anything that didn't parse in the box, so the user can fix it.
        NewEntryBox.Text = string.Join(", ", rejected);
        EntryProblemText.Text = string.Join(Environment.NewLine, problems);
        EntryProblemText.Visibility = problems.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
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
        if (!TryParsePort(UpstreamPortBox.Text, out var upstreamPort) || !TryParsePort(ListenPortBox.Text, out var listenPort))
        {
            ProblemText.Text = "Ports must be numbers from 1 to 65535.";
            return;
        }

        var settings = new UserSettings(
            listenPort,
            UpstreamHostBox.Text.Trim(),
            upstreamPort,
            [.. _entries],
            DenyLocalBox.IsChecked == true,
            StartAtLoginBox.IsChecked == true);
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
