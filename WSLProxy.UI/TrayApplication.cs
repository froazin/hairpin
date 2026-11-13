using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WSLProxy.Relay.Configuration;
using WSLProxy.Relay.Status;
using WSLProxy.UI.Diagnostics;
using WSLProxy.UI.Settings;
using WSLProxy.UI.Tray;
using WSLProxy.UI.Views;

namespace WSLProxy.UI;

/// <summary>
/// The tray app: a WPF application with no main window, a notification area icon, the flyout and the settings window.
/// Create it and call <see cref="Run"/> on an STA thread.
/// </summary>
public sealed partial class TrayApplication : IDisposable
{
    private readonly IRelayStatus _status;
    private readonly IRelayControl _control;
    private readonly IOptionsMonitor<RelayOptions> _relayOptions;
    private readonly IOptionsMonitor<AppOptions> _appOptions;
    private readonly UserSettingsStore _store;
    private readonly StartupRegistration _startup;
    private readonly LogStore _logStore;
    private readonly IDebugLogging _debugLogging;
    private readonly ConfigChecker _checker;
    private readonly IRelayMetrics _metrics;
    private TrafficMonitor? _monitor;
    private readonly string _logDirectory;
    private readonly ILogger<TrayApplication> _logger;
    private Application? _application;
    private TrayIcon? _tray;
    private FlyoutWindow? _flyout;
    private SettingsWindow? _settings;
    private LogWindow? _logs;
    private IReadOnlyList<ConfigProblem> _problems = [];

    /// <summary>Creates the tray app.</summary>
    public TrayApplication(IRelayStatus status, IRelayControl control, IRelayMetrics metrics, IOptionsMonitor<RelayOptions> relayOptions,
        IOptionsMonitor<AppOptions> appOptions, UserSettingsStore store, StartupRegistration startup, LogStore logStore,
        IDebugLogging debugLogging, ConfigChecker checker, string logDirectory,
        ILogger<TrayApplication> logger)
    {
        _status = status;
        _control = control;
        _metrics = metrics;
        _relayOptions = relayOptions;
        _appOptions = appOptions;
        _store = store;
        _startup = startup;
        _logStore = logStore;
        _debugLogging = debugLogging;
        _checker = checker;
        _logDirectory = logDirectory;
        _logger = logger;
    }

    /// <summary>Runs the UI until the user quits. Returns the process exit code.</summary>
    public int Run()
    {
        _application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

        // Follow Windows' light or dark app theme with the Fluent styles. ThemeMode is marked experimental (WPF0001) in .NET 10;
        // the documented fallback is to merge Themes/Fluent.xaml by hand if it changes.
#pragma warning disable WPF0001
        _application.ThemeMode = ThemeMode.System;
#pragma warning restore WPF0001

        _application.DispatcherUnhandledException += (_, e) =>
        {
            // Log the type only, and keep the tray running: one bad click mustn't take the relay down.
            LogUiError(_logger, e.Exception.GetType().FullName ?? nameof(Exception));
            e.Handled = true;
        };

        _flyout = new FlyoutWindow();
        _flyout.PauseRequested += async (_, pause) =>
        {
            if (pause)
            {
                await _control.PauseAsync().ConfigureAwait(true);
            }
            else
            {
                await _control.ResumeAsync().ConfigureAwait(true);
            }
        };
        _flyout.CopyRequested += (_, _) => CopyWslSetup();
        _flyout.SettingsRequested += (_, _) => ShowSettings();
        _flyout.LogsRequested += (_, _) => ShowLogs();
        _flyout.DebugLoggingRequested += (_, enabled) => SetDebugLogging(enabled);
        _flyout.SetDebugLogging(_debugLogging.IsEnabled);
        _flyout.QuitRequested += (_, _) => _application.Shutdown();

        _tray = new TrayIcon();
        _tray.Activated += (_, anchor) =>
        {
            _checker.CheckSoon(TimeSpan.Zero);
            ShowTraffic();
            _flyout.Toggle(anchor);
        };
        _tray.DoubleClicked += (_, anchor) =>
        {
            _checker.CheckSoon(TimeSpan.Zero);
            ShowTraffic();
            _flyout.Open(anchor);
        };

        // Remove the icon while its window still exists. After Run returns, WPF has destroyed it and NIM_DELETE would fail.
        _application.Exit += (_, _) => _tray.Dispose();

        _status.Changed += OnStatusChanged;
        Apply(_status.Current);
        _checker.Changed += OnConfigChanged;
        _checker.Start();
        _monitor = new TrafficMonitor(_metrics);
        _monitor.Sampled += OnTrafficSampled;
        try
        {
            return _application.Run();
        }
        finally
        {
            _status.Changed -= OnStatusChanged;
            _checker.Changed -= OnConfigChanged;
            _monitor.Sampled -= OnTrafficSampled;
            _monitor.Dispose();
            _tray.Dispose();
            _flyout.Close();
        }
    }

    /// <summary>Removes the tray icon if <see cref="Run"/> didn't get the chance to.</summary>
    public void Dispose() => _tray?.Dispose();

    /// <summary>The setup lines to paste into <c>~/.profile</c> in WSL.</summary>
    public static string WslSetup(int port)
    {
        var url = "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture);
        return string.Join('\n',
            "# WSL Proxy Relay: add to ~/.profile",
            $"export http_proxy={url} https_proxy={url}",
            $"export HTTP_PROXY={url} HTTPS_PROXY={url}",
            "export no_proxy=localhost,127.0.0.1 NO_PROXY=localhost,127.0.0.1",
            string.Empty);
    }

    private void OnStatusChanged(object? sender, RelaySnapshot snapshot) =>
        _application?.Dispatcher.BeginInvoke(DispatcherPriority.Background, () => Apply(_status.Current));

    private void Apply(RelaySnapshot snapshot)
    {
        _flyout?.Update(snapshot);
        var state = snapshot.State switch
        {
            RelayState.Running => "running on " + snapshot.ListenEndpoint,
            RelayState.Paused => "paused",
            RelayState.Faulted => "needs attention",
            RelayState.Stopped => "stopped",
            _ => "starting",
        };
        var problems = _problems.Count switch
        {
            0 => string.Empty,
            1 => " · 1 setting needs fixing",
            _ => " · " + _problems.Count.ToString(CultureInfo.InvariantCulture) + " settings need fixing",
        };
        _tray?.SetTooltip("WSL Proxy Relay: " + state + problems);
    }

    // Raised on a thread-pool thread once a second. Redraw only while the flyout is open.
    private void OnTrafficSampled(object? sender, EventArgs e) =>
        _application?.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (_flyout?.IsVisible == true)
            {
                ShowTraffic();
            }
        });

    private void ShowTraffic()
    {
        if (_monitor is not null)
        {
            _flyout?.UpdateTraffic(_monitor.Summary(FlyoutWindow.ChartPoints));
        }
    }

    // Raised on a thread-pool thread. Log here, then update the UI on its own thread.
    private void OnConfigChanged(object? sender, EventArgs e)
    {
        var problems = _checker.Problems;
        foreach (var problem in problems)
        {
            LogConfigProblem(_logger, problem.Title, problem.Fix);
        }

        if (problems.Count == 0)
        {
            LogConfigFine(_logger);
        }

        _application?.Dispatcher.BeginInvoke(DispatcherPriority.Background, () => ApplyProblems(_checker.Problems));
    }

    private void ApplyProblems(IReadOnlyList<ConfigProblem> problems)
    {
        _problems = problems;
        _flyout?.SetProblems(problems);
        _tray?.SetBadge(problems.Count > 0);
        Apply(_status.Current);
    }

    private void CopyWslSetup()
    {
        try
        {
            Clipboard.SetText(WslSetup(_relayOptions.CurrentValue.ListenPort));
            _flyout?.ShowCopied();
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // Another app is holding the clipboard. The user can click again.
        }
    }

    private void ShowSettings()
    {
        if (_settings is not null)
        {
            _settings.Activate();
            return;
        }

        var current = UserSettings.From(_relayOptions.CurrentValue, _appOptions.CurrentValue);
        if (_startup.Enabled)
        {
            // Show what Windows will actually do, including a Task Manager "disabled" mark.
            current = current with { StartAtLogin = StartupRegistration.IsEffective() };
        }

        _settings = new SettingsWindow(current, Save);
        _settings.Closed += (_, _) => _settings = null;
        _settings.Show();
        _settings.Activate();
    }

    private IReadOnlyList<string> Save(UserSettings settings)
    {
        var problems = _store.Save(settings, _relayOptions.CurrentValue);
        if (problems.Count == 0 && settings.StartAtLogin != StartupRegistration.IsEffective() && !_startup.ApplyUserChoice(settings.StartAtLogin))
        {
            return ["The settings were saved, but Windows didn't let the app change its start-at-sign-in entry."];
        }

        return problems;
    }

    private void ShowLogs()
    {
        if (_logs is not null)
        {
            _logs.Activate();
            return;
        }

        _logs = new LogWindow(_logStore, OpenLogs);
        _logs.Closed += (_, _) => _logs = null;
        _logs.Show();
        _logs.Activate();
    }

    private void SetDebugLogging(bool enabled)
    {
        _debugLogging.SetEnabled(enabled);
        _flyout?.SetDebugLogging(_debugLogging.IsEnabled);
        LogDebugLogging(_logger, enabled ? "on" : "off");
    }

    private void OpenLogs()
    {
        Directory.CreateDirectory(_logDirectory);
        using var _ = Process.Start(new ProcessStartInfo { FileName = _logDirectory, UseShellExecute = true });
    }

    [LoggerMessage(EventId = 101, Level = LogLevel.Information, Message = "Debug logging turned {State} until the app restarts")]
    private static partial void LogDebugLogging(ILogger logger, string state);

    [LoggerMessage(EventId = 102, Level = LogLevel.Warning, Message = "Settings check: {Title} Fix: {Fix}")]
    private static partial void LogConfigProblem(ILogger logger, string title, string fix);

    [LoggerMessage(EventId = 103, Level = LogLevel.Information, Message = "Settings check: IPv6 and the WSL networking mode allow mirrored mode")]
    private static partial void LogConfigFine(ILogger logger);

    [LoggerMessage(EventId = 100, Level = LogLevel.Error, Message = "Unexpected UI error: {ExceptionType}")]
    private static partial void LogUiError(ILogger logger, string exceptionType);
}
