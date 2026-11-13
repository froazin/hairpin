using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using WSLProxy.Relay;
using WSLProxy.Relay.Configuration;

namespace WSLProxy.UI.Settings;

/// <summary>App settings that aren't part of the relay, bound from the <c>App</c> configuration section.</summary>
public sealed class AppOptions
{
    /// <summary>The configuration section these options bind from.</summary>
    public const string SectionName = "App";

    /// <summary>Starts the app when the user signs in to Windows. On by default.</summary>
    public bool StartAtLogin { get; set; } = true;

    /// <summary>How many days of log files to keep.</summary>
    public int LogRetentionDays { get; set; } = 7;
}

/// <summary>The settings a user can change in the settings window.</summary>
/// <param name="ListenPort">The relay's port on 127.0.0.1.</param>
/// <param name="UpstreamHost">The upstream proxy's host, or empty for none.</param>
/// <param name="UpstreamPort">The upstream proxy's port.</param>
/// <param name="NoProxy">The bypass list entries, in order.</param>
/// <param name="DenyLocalDestinations">Whether local and reserved destinations are refused.</param>
/// <param name="StartAtLogin">Whether the app starts when the user signs in.</param>
public sealed record UserSettings(
    int ListenPort,
    string UpstreamHost,
    int UpstreamPort,
    IReadOnlyList<string> NoProxy,
    bool DenyLocalDestinations,
    bool StartAtLogin)
{
    /// <summary>Takes the values currently in force.</summary>
    public static UserSettings From(RelayOptions relay, AppOptions app)
    {
        ArgumentNullException.ThrowIfNull(relay);
        ArgumentNullException.ThrowIfNull(app);
        return new UserSettings(relay.ListenPort, relay.Upstream.Host, relay.Upstream.Port, [.. relay.NoProxy], relay.DenyLocalDestinations, app.StartAtLogin);
    }

    /// <summary>Builds the relay options these settings would produce, keeping every other value from <paramref name="current"/>.</summary>
    public RelayOptions ToRelayOptions(RelayOptions current)
    {
        ArgumentNullException.ThrowIfNull(current);
        var options = new RelayOptions
        {
            ListenPort = ListenPort,
            Upstream = new UpstreamOptions
            {
                Host = UpstreamHost.Trim(),
                Port = UpstreamPort,
                HealthCheckIntervalSeconds = current.Upstream.HealthCheckIntervalSeconds,
            },
            DenyLocalDestinations = DenyLocalDestinations,
            Limits = current.Limits,
        };
        foreach (var entry in NoProxy)
        {
            options.NoProxy.Add(entry);
        }

        return options;
    }
}

/// <summary>
/// Reads and writes the user's settings file, <c>%LOCALAPPDATA%\WSLProxy\settings.json</c>. Configuration layers it over
/// <c>appsettings.json</c> and reloads it when it changes, so saving here applies the settings live.
/// </summary>
public sealed class UserSettingsStore
{
    /// <summary>Creates a store for the file at <paramref name="path"/>.</summary>
    public UserSettingsStore(string path) => FilePath = path;

    /// <summary>The settings file's path.</summary>
    public string FilePath { get; }

    /// <summary>
    /// Validates and saves the settings. The file is written to a temporary file first and then moved into place, so a crash
    /// can't leave a half-written file for the relay to read.
    /// </summary>
    /// <returns>The validation problems; empty when the settings were saved.</returns>
    public IReadOnlyList<string> Save(UserSettings settings, RelayOptions current)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var problems = ServiceCollectionExtensions.ValidateRelayOptions(settings.ToRelayOptions(current));
        if (problems.Count > 0)
        {
            return problems;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var bytes = Serialise(settings, ReadExisting());
            var temporary = FilePath + ".tmp";
            using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                file.Write(bytes);
                file.Flush(flushToDisk: true);
            }

            // The relay's reload may briefly hold the file open; try a few times before giving up.
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    File.Move(temporary, FilePath, overwrite: true);
                    return [];
                }
                catch (IOException) when (attempt < 5)
                {
                    Thread.Sleep(100 * attempt);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return ["Couldn't save the settings file: " + ex.Message];
        }
    }

    /// <summary>
    /// Writes the settings into the JSON configuration layout: a <c>Relay</c> section and an <c>App</c> section. Keys the
    /// settings window doesn't own, such as limits a user added by hand, are kept from <paramref name="existing"/>.
    /// </summary>
    internal static byte[] Serialise(UserSettings settings, JsonObject? existing = null)
    {
        var root = existing ?? [];
        var relay = Section(root, RelayOptions.SectionName);
        relay[nameof(RelayOptions.ListenPort)] = settings.ListenPort;
        var upstream = Section(relay, nameof(RelayOptions.Upstream));
        upstream[nameof(UpstreamOptions.Host)] = settings.UpstreamHost.Trim();
        upstream[nameof(UpstreamOptions.Port)] = settings.UpstreamPort;
        relay[nameof(RelayOptions.NoProxy)] = new JsonArray([.. settings.NoProxy.Select(e => (JsonNode?)JsonValue.Create(e.Trim()))]);
        relay[nameof(RelayOptions.DenyLocalDestinations)] = settings.DenyLocalDestinations;
        Section(root, AppOptions.SectionName)[nameof(AppOptions.StartAtLogin)] = settings.StartAtLogin;
        return Encoding.UTF8.GetBytes(root.ToJsonString(WriteOptions) + Environment.NewLine);
    }

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    /// <summary>Reads the current file, matching keys case-insensitively as configuration does. Null when it's missing or not valid JSON.</summary>
    private JsonObject? ReadExisting()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonNode.Parse(File.ReadAllText(FilePath), new JsonNodeOptions { PropertyNameCaseInsensitive = true }) as JsonObject
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonObject Section(JsonObject parent, string name)
    {
        if (parent[name] is JsonObject section)
        {
            return section;
        }

        var created = new JsonObject();
        parent[name] = created;
        return created;
    }
}
