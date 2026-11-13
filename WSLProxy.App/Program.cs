using System.Globalization;
using System.IO;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WSLProxy.Relay;
using WSLProxy.Relay.Configuration;
using WSLProxy.Relay.Status;
using WSLProxy.UI;
using WSLProxy.UI.Settings;

namespace WSLProxy.App;

/// <summary>Entry point for WSLProxyRelay.exe.</summary>
public static partial class Program
{
#if DEBUG
    private const bool IsReleaseBuild = false;
#else
    private const bool IsReleaseBuild = true;
#endif

    private static string _dataDirectory = string.Empty;

    /// <summary>Starts the relay and the tray UI, and runs until the user quits.</summary>
    [STAThread]
    public static int Main(string[] args)
    {
        _dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WSLProxy");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => RecordCrash(e.ExceptionObject as Exception);
        try
        {
            return Run(args);
        }
        catch (Exception ex)
        {
            // Anything that gets this far would otherwise end the process with no trace. Record it and tell the user.
            RecordCrash(ex);
            System.Windows.MessageBox.Show(
                "WSL Proxy Relay couldn't start. Details are in " + Path.Combine(_dataDirectory, "crash.log") + ".",
                "WSL Proxy Relay", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            return 1;
        }
    }

    private static int Run(string[] args)
    {
        // One instance per user and session. The name is predictable, so another process in the session could hold it
        // first; then this instance just doesn't start, which is the safe outcome.
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? "unknown";
        Mutex instance;
        bool isFirst;
        try
        {
            instance = new Mutex(initiallyOwned: true, @"Local\WSLProxyRelay-" + sid, out isFirst);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
        {
            return 0;
        }

        using (instance)
        {
            if (!isFirst)
            {
                return 0;
            }

            return RunFirstInstance(args);
        }
    }

    private static int RunFirstInstance(string[] args)
    {
        var logDirectory = Path.Combine(_dataDirectory, "logs");
        var settingsFile = Path.Combine(_dataDirectory, "settings.json");
        Directory.CreateDirectory(_dataDirectory);
        var setAside = SetAsideIfCorrupt(settingsFile);

        // An empty builder: no environment variables, command line or user secrets can override the settings.
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args,
            ApplicationName = "WSL Proxy Relay",
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Configuration
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: false, reloadOnChange: false)
            .AddJsonFile(source =>
            {
                source.Path = Path.GetFileName(settingsFile);
                source.FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(_dataDirectory);
                source.Optional = true;
                source.ReloadOnChange = true;

                // A half-edited file mustn't crash the app: ignore it and keep the previous values.
                source.OnLoadException = context => context.Ignore = true;
            });

        var appSection = builder.Configuration.GetSection(AppOptions.SectionName);
        var retention = int.TryParse(appSection[nameof(AppOptions.LogRetentionDays)], NumberStyles.None, CultureInfo.InvariantCulture, out var days) ? days : 7;

        // Declared before the host so it's disposed after it, and shutdown messages still reach the file.
        using var fileLogger = new FileLoggerProvider(Path.Combine(_dataDirectory, "logs"), retention);
        builder.Logging.AddConfiguration(builder.Configuration.GetSection("Logging"));
        builder.Logging.AddProvider(fileLogger);

        builder.Services.AddRelay(builder.Configuration);
        builder.Services.AddSafeOptions<AppOptions>(builder.Configuration, AppOptions.SectionName);
        builder.Services.AddSingleton(new UserSettingsStore(settingsFile));
        builder.Services.AddSingleton(new StartupRegistration(
            Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "WSLProxyRelay.exe"),
            enabled: IsReleaseBuild));
        builder.Services.AddSingleton(sp => new TrayApplication(
            sp.GetRequiredService<IRelayStatus>(),
            sp.GetRequiredService<IRelayControl>(),
            sp.GetRequiredService<IOptionsMonitor<RelayOptions>>(),
            sp.GetRequiredService<IOptionsMonitor<AppOptions>>(),
            sp.GetRequiredService<UserSettingsStore>(),
            sp.GetRequiredService<StartupRegistration>(),
            logDirectory,
            sp.GetRequiredService<ILogger<TrayApplication>>()));

        using var host = builder.Build();
        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("WSLProxy.App");
        if (setAside is not null)
        {
            LogSetAside(logger, setAside);
        }

        host.Start();

        // Development builds never touch the start-at-sign-in entry (StartupRegistration is disabled in Debug).
        var startAtLogin = host.Services.GetRequiredService<IOptionsMonitor<AppOptions>>().CurrentValue.StartAtLogin;
        if (!host.Services.GetRequiredService<StartupRegistration>().ApplyAtLaunch(startAtLogin))
        {
            LogStartupRegistrationFailed(logger);
        }

        int exitCode;
        using (var tray = host.Services.GetRequiredService<TrayApplication>())
        {
            exitCode = tray.Run();
        }

        host.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        return exitCode;
    }

    /// <summary>
    /// Renames a settings file that isn't valid JSON to <c>settings.json.invalid-&lt;time&gt;</c>, so the app starts with its
    /// defaults instead of failing at every start. Returns the new name, or null when the file was fine or missing.
    /// </summary>
    private static string? SetAsideIfCorrupt(string settingsFile)
    {
        try
        {
            if (!File.Exists(settingsFile))
            {
                return null;
            }

            using (JsonDocument.Parse(File.ReadAllText(settingsFile), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }))
            {
                return null;
            }
        }
        catch (JsonException)
        {
            var target = settingsFile + ".invalid-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
            File.Move(settingsFile, target, overwrite: true);
            return Path.GetFileName(target);
        }
    }

    private static void RecordCrash(Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(_dataDirectory);

            // The type and stack trace only: exception messages can carry peer data.
            File.AppendAllText(Path.Combine(_dataDirectory, "crash.log"),
                DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) + " " + (ex?.GetType().FullName ?? "unknown")
                + Environment.NewLine + ex?.StackTrace + Environment.NewLine);
        }
        catch (Exception logEx) when (logEx is IOException or UnauthorizedAccessException)
        {
            // Nowhere left to record it.
        }
    }

    [LoggerMessage(EventId = 200, Level = LogLevel.Warning, Message = "settings.json wasn't valid JSON, so it was renamed to {FileName} and the defaults are in use")]
    private static partial void LogSetAside(ILogger logger, string fileName);

    [LoggerMessage(EventId = 201, Level = LogLevel.Warning, Message = "Couldn't update the start-at-sign-in entry in the registry")]
    private static partial void LogStartupRegistrationFailed(ILogger logger);
}
