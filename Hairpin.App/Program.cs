using System.Globalization;
using System.IO;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Hairpin.Engine;
using Hairpin.Engine.Configuration;
using Hairpin.Engine.Status;
using Hairpin.UI;
using Hairpin.UI.Diagnostics;
using Hairpin.UI.Interop;
using Hairpin.UI.Settings;

namespace Hairpin.App;

/// <summary>Entry point for Hairpin.exe.</summary>
public static partial class Program
{
#if DEBUG
    private const bool IsReleaseBuild = false;
#else
    private const bool IsReleaseBuild = true;
#endif

    private static string _dataDirectory = string.Empty;

    // Null for the zip build; the package family name when the app runs from its MSIX package.
    private static string? _packageFamilyName;

    /// <summary>Starts the relay and the tray UI, and runs until the user quits.</summary>
    [STAThread]
    public static int Main(string[] args)
    {
        _packageFamilyName = PackageIdentity.FamilyName();
        _dataDirectory = DataDirectory(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), _packageFamilyName);
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
                "Hairpin couldn't start. Details are in " + Path.Combine(_dataDirectory, "crash.log") + ".",
                "Hairpin", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
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
            instance = new Mutex(initiallyOwned: true, @"Local\Hairpin-" + sid, out isFirst);
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
            ApplicationName = "Hairpin",
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

        // Last, so the tray's debug logging switch overrides the log levels in both files while it's on.
        var debugLogging = new DebugLoggingSwitch();
        ((IConfigurationBuilder)builder.Configuration).Add(debugLogging);

        var appSection = builder.Configuration.GetSection(AppOptions.SectionName);
        var retention = int.TryParse(appSection[nameof(AppOptions.LogRetentionDays)], NumberStyles.None, CultureInfo.InvariantCulture, out var days) ? days : 7;
        var logLines = int.TryParse(appSection[nameof(AppOptions.LogWindowLines)], NumberStyles.None, CultureInfo.InvariantCulture, out var lines) ? lines : 1000;
        var logStore = new LogStore(Math.Clamp(logLines, 100, 10_000));

        // Declared before the host so it's disposed after it, and shutdown messages still reach the file.
        using var fileLogger = new FileLoggerProvider(Path.Combine(_dataDirectory, "logs"), retention);
        builder.Logging.AddConfiguration(builder.Configuration.GetSection("Logging"));
        builder.Logging.AddProvider(fileLogger);
        builder.Logging.AddProvider(new MemoryLoggerProvider(logStore));

        builder.Services.AddRelay(builder.Configuration);
        builder.Services.AddSafeOptions<AppOptions>(builder.Configuration, AppOptions.SectionName);
        builder.Services.AddSingleton(new UserSettingsStore(settingsFile));
        builder.Services.AddSingleton(_ => new ConfigChecker(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".wslconfig")));
        builder.Services.AddSingleton(_packageFamilyName is not null
            ? StartupRegistration.ForPackage()
            : new StartupRegistration(
                Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Hairpin.exe"),
                enabled: IsReleaseBuild));
        builder.Services.AddSingleton(sp => new TrayApplication(
            sp.GetRequiredService<IRelayStatus>(),
            sp.GetRequiredService<IRelayControl>(),
            sp.GetRequiredService<IRelayMetrics>(),
            sp.GetRequiredService<IOptionsMonitor<RelayOptions>>(),
            sp.GetRequiredService<IOptionsMonitor<AppOptions>>(),
            sp.GetRequiredService<UserSettingsStore>(),
            sp.GetRequiredService<StartupRegistration>(),
            logStore,
            debugLogging,
            sp.GetRequiredService<ConfigChecker>(),
            logDirectory,
            sp.GetRequiredService<ILogger<TrayApplication>>()));

        using var host = builder.Build();
        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Hairpin.App");
        if (setAside is not null)
        {
            LogSetAside(logger, setAside);
        }

        host.Start();

        // Development builds never touch the start-at-sign-in entry (StartupRegistration is disabled in Debug), and nor
        // does the MSIX package, whose startup task starts it instead.
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
    /// The folder for settings.json, the logs and crash.log: <c>%LOCALAPPDATA%\Hairpin</c> for the zip build, or the
    /// package's own <c>LocalState</c> folder when the app runs from its MSIX package. Windows redirects a packaged app's
    /// new files elsewhere under <c>%LOCALAPPDATA%</c> to a private location Explorer can't see, but not its own folder.
    /// </summary>
    internal static string DataDirectory(string localAppData, string? packageFamilyName) =>
        packageFamilyName is null
            ? Path.Combine(localAppData, "Hairpin")
            : Path.Combine(localAppData, "Packages", packageFamilyName, "LocalState");

    /// <summary>
    /// Renames a settings file that isn't valid JSON to <c>settings.json.invalid-&lt;time&gt;</c>, so the app starts with its
    /// defaults instead of failing at every start. Returns the new name, or null when the file was fine or missing.
    /// </summary>
    internal static string? SetAsideIfCorrupt(string settingsFile)
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
            // Not valid JSON: set it aside below.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Locked or unreadable, perhaps by an editor or antivirus. Leave it: configuration ignores a file it can't load.
            return null;
        }

        try
        {
            var target = settingsFile + ".invalid-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
            File.Move(settingsFile, target, overwrite: true);
            return Path.GetFileName(target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Couldn't rename it. Start anyway: configuration ignores a file it can't load.
            return null;
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
