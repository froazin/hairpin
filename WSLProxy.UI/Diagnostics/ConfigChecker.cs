using System.IO;
using System.Security;
using Microsoft.Win32;

namespace WSLProxy.UI.Diagnostics;

/// <summary>
/// Reads the settings <see cref="ConfigChecks"/> needs from the registry and <c>.wslconfig</c>, and checks them again when
/// <c>.wslconfig</c> changes or the UI asks. It reads off the UI thread, never throws, and treats anything it can't read as
/// not set.
/// </summary>
public sealed class ConfigChecker : IDisposable
{
    private const string Tcpip6Key = @"SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters";
    private const string WslPolicyKey = @"SOFTWARE\Policies\WSL";

    // .wslconfig is a few lines long. Anything bigger isn't one, and isn't worth reading.
    private const long MaxWslConfigBytes = 1024 * 1024;

    private readonly string _wslConfigPath;
    private readonly Lock _lock = new();
    private readonly Timer _debounce;
    private readonly Lock _checkLock = new();
    private int _pending;
    private FileSystemWatcher? _watcher;
    private IReadOnlyList<ConfigProblem> _problems = [];

    /// <summary>Creates a checker for the <c>.wslconfig</c> at <paramref name="wslConfigPath"/>.</summary>
    public ConfigChecker(string wslConfigPath)
    {
        _wslConfigPath = wslConfigPath;
        _debounce = new Timer(_ => Check());
    }

    /// <summary>Raised on a thread-pool thread when the problems found change.</summary>
    public event EventHandler? Changed;

    /// <summary>The problems found by the latest check, empty when there are none.</summary>
    public IReadOnlyList<ConfigProblem> Problems
    {
        get
        {
            lock (_lock)
            {
                return _problems;
            }
        }
    }

    /// <summary>Checks now, then again whenever <c>.wslconfig</c> is created, changed, renamed or deleted.</summary>
    public void Start()
    {
        CheckSoon(TimeSpan.Zero);
        var directory = Path.GetDirectoryName(_wslConfigPath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            return;
        }

        try
        {
            _watcher = new FileSystemWatcher(directory, Path.GetFileName(_wslConfigPath))
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            FileSystemEventHandler onChange = (_, _) => CheckSoon(TimeSpan.FromMilliseconds(500));
            _watcher.Changed += onChange;
            _watcher.Created += onChange;
            _watcher.Deleted += onChange;
            _watcher.Renamed += (_, _) => CheckSoon(TimeSpan.FromMilliseconds(500));
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            // No watcher: the checker still runs each time the flyout opens.
            _watcher?.Dispose();
            _watcher = null;
        }
    }

    /// <summary>
    /// Checks again on a thread-pool thread after <paramref name="delay"/>. Editors save in bursts, so a new request
    /// replaces one that hasn't run yet.
    /// </summary>
    public void CheckSoon(TimeSpan delay)
    {
        try
        {
            _debounce.Change(delay, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // A file event or a click that arrives while the app shuts down.
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _watcher?.Dispose();
        _debounce.Dispose();
    }

    // Runs one check at a time. A request that arrives during a check makes it run once more afterwards, so a slower,
    // older read can never overwrite a newer one.
    private void Check()
    {
        if (Interlocked.Exchange(ref _pending, 1) == 1)
        {
            return;
        }

        lock (_checkLock)
        {
            while (Interlocked.Exchange(ref _pending, 0) == 1)
            {
                CheckOnce();
            }
        }
    }

    private void CheckOnce()
    {
        var problems = new List<ConfigProblem>();
        if (ConfigChecks.CheckIpv6(ReadDword(Tcpip6Key, "DisabledComponents")) is { } ipv6)
        {
            problems.Add(ipv6);
        }

        var mode = ConfigChecks.CheckNetworkingMode(
            ReadNetworkingMode(),
            ReadDword(WslPolicyKey, "DefaultNetworkingMode"),
            ReadDword(WslPolicyKey, "AllowNetworkingModeUserSetting"));
        if (mode is not null)
        {
            problems.Add(mode);
        }

        lock (_lock)
        {
            if (problems.SequenceEqual(_problems))
            {
                return;
            }

            _problems = problems;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private string? ReadNetworkingMode()
    {
        try
        {
            var file = new FileInfo(_wslConfigPath);
            return file.Exists && file.Length <= MaxWslConfigBytes ? ConfigChecks.ReadNetworkingMode(File.ReadAllText(file.FullName)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return null;
        }
    }

    private static int? ReadDword(string keyPath, string name)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(keyPath);
            return key?.GetValue(name) as int?;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return null;
        }
    }
}
