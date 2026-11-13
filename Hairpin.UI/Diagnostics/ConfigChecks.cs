namespace Hairpin.UI.Diagnostics;

/// <summary>A Windows or WSL setting that stops mirrored mode, and the one-line fix.</summary>
/// <param name="Title">What's wrong, in one short sentence.</param>
/// <param name="Fix">How to fix it, in one short sentence.</param>
public sealed record ConfigProblem(string Title, string Fix);

/// <summary>WSL's networking modes, with the values its <c>DefaultNetworkingMode</c> policy uses.</summary>
public enum NetworkingMode
{
    /// <summary>No networking.</summary>
    None = 0,

    /// <summary>NAT, WSL's default.</summary>
    Nat = 1,

    /// <summary>Bridged to a Hyper-V switch.</summary>
    Bridged = 2,

    /// <summary>Mirrored, which the relay needs.</summary>
    Mirrored = 3,

    /// <summary>Consomme, called <c>virtioproxy</c> on WSL 2.x.</summary>
    Consomme = 4,
}

/// <summary>
/// Checks the two settings that most often stop WSL from using mirrored mode. Pure: <see cref="ConfigChecker"/> reads the
/// registry and <c>.wslconfig</c> and passes the values in. Both checks follow WSL's own source
/// (<c>WslCoreVm.cpp</c> and <c>WslCoreConfig.cpp</c> in microsoft/WSL).
/// </summary>
public static class ConfigChecks
{
    /// <summary>The <c>DisabledComponents</c> value that turns IPv6 off completely. WSL then falls back from mirrored to NAT.</summary>
    public const int Ipv6FullyDisabled = 0xFF;

    /// <summary>
    /// A problem when <c>DisabledComponents</c> is exactly <c>0xFF</c>, the only value WSL refuses mirrored mode for. Other
    /// values, such as <c>0x20</c> (prefer IPv4), are fine.
    /// </summary>
    public static ConfigProblem? CheckIpv6(int? disabledComponents) =>
        disabledComponents == Ipv6FullyDisabled
            ? new ConfigProblem(
                "IPv6 is fully disabled, so WSL won't use mirrored mode.",
                "Set DisabledComponents to 0x20 under HKLM\\SYSTEM\\CurrentControlSet\\Services\\Tcpip6\\Parameters, then restart Windows.")
            : null;

    /// <summary>
    /// A problem when the networking mode WSL will use isn't mirrored. That's the <c>.wslconfig</c> value, unless policy
    /// forbids users to choose; without one it's the <c>DefaultNetworkingMode</c> policy, or NAT.
    /// </summary>
    /// <param name="wslConfigValue">The <c>networkingMode</c> value from <c>.wslconfig</c>, or null when it's not set.</param>
    /// <param name="policyDefault">The <c>DefaultNetworkingMode</c> policy, or null when it's not set.</param>
    /// <param name="policyAllowUserSetting">The <c>AllowNetworkingModeUserSetting</c> policy, or null when it's not set.</param>
    public static ConfigProblem? CheckNetworkingMode(string? wslConfigValue, int? policyDefault, int? policyAllowUserSetting)
    {
        // Bridged needs more settings, so WSL ignores it as a default, like any unknown value.
        var defaultMode = policyDefault is 0 or 1 or 3 or 4 ? (NetworkingMode)policyDefault.Value : NetworkingMode.Nat;

        // Only 0 forbids: a missing or invalid policy value allows it.
        var userMayChoose = policyAllowUserSetting != 0;
        var mode = defaultMode;
        var byPolicy = policyDefault is not null;
        if (TryParseMode(wslConfigValue, out var chosen))
        {
            // Users can always turn networking off, whatever the policy says.
            mode = !userMayChoose && chosen != NetworkingMode.None && chosen != defaultMode ? defaultMode : chosen;
            byPolicy = mode != chosen;
        }

        if (mode == NetworkingMode.Mirrored)
        {
            return null;
        }

        var name = mode == NetworkingMode.Nat ? "NAT" : mode.ToString().ToLowerInvariant();
        return byPolicy && !userMayChoose
            ? new ConfigProblem(
                $"Your organisation's WSL policy sets the networking mode to {name}, not mirrored.",
                "Ask IT to set the DefaultNetworkingMode policy to mirrored, or to allow users to choose it.")
            : new ConfigProblem(
                $"WSL is set to {name} networking, not mirrored.",
                "Run wsl --shutdown after adding networkingMode=mirrored under [wsl2] in %UserProfile%\\.wslconfig.");
    }

    /// <summary>Reads a networking mode name as WSL does: ignoring case, with <c>virtioproxy</c> as the old name for consomme.</summary>
    public static bool TryParseMode(string? value, out NetworkingMode mode)
    {
        mode = NetworkingMode.Nat;
        switch (value?.Trim().ToUpperInvariant())
        {
            case "NONE": mode = NetworkingMode.None; return true;
            case "NAT": mode = NetworkingMode.Nat; return true;
            case "BRIDGED": mode = NetworkingMode.Bridged; return true;
            case "MIRRORED": mode = NetworkingMode.Mirrored; return true;
            case "CONSOMME" or "VIRTIOPROXY": mode = NetworkingMode.Consomme; return true;
            default: return false;
        }
    }

    /// <summary>
    /// Reads <c>networkingMode</c> from the text of a <c>.wslconfig</c> file, as WSL does: under <c>[wsl2]</c> or the older
    /// <c>[experimental]</c>, with section and key names in any case, <c>#</c> starting a comment, values optionally in double
    /// quotes, and the last value winning. Returns null when it's not set.
    /// </summary>
    public static string? ReadNetworkingMode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string? section = null;
        string? value = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = StripComment(raw).Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line[0] == '[')
            {
                var end = line.IndexOf(']', StringComparison.Ordinal);
                section = end > 0 ? line[1..end].Trim() : null;
                continue;
            }

            var equals = line.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0
                || !line[..equals].Trim().Equals("networkingMode", StringComparison.OrdinalIgnoreCase)
                || !(string.Equals(section, "wsl2", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(section, "experimental", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var found = line[(equals + 1)..].Trim();
            value = found.Length >= 2 && found[0] == '"' && found[^1] == '"' ? found[1..^1] : found;
        }

        return value;
    }

    // A # starts a comment, except inside double quotes.
    private static string StripComment(string line)
    {
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"')
            {
                quoted = !quoted;
            }
            else if (line[i] == '#' && !quoted)
            {
                return line[..i];
            }
        }

        return line;
    }
}
