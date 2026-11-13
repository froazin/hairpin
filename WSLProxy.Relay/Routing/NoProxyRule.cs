using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;

namespace WSLProxy.Relay.Routing;

/// <summary>One entry in the bypass (no_proxy) list. Immutable and thread-safe.</summary>
/// <remarks>
/// Grammar, one entry per string, surrounding whitespace trimmed:
/// <list type="table">
/// <item><term><c>*</c></term><description>Every host and port. Only valid on its own.</description></item>
/// <item><term><c>example.com</c> or <c>.example.com</c></term><description>The name and every subdomain, on a label boundary: <c>badexample.com</c> doesn't match.</description></item>
/// <item><term><c>*.example.com</c></term><description>Subdomains only, at any depth. Not the name itself. The only glob allowed: <c>ex*.com</c> and <c>a.*.b</c> are rejected.</description></item>
/// <item><term><c>10.1.2.3</c></term><description>That IPv4 address. Strict dotted quad only.</description></item>
/// <item><term><c>10.0.0.0/8</c></term><description>That IPv4 network. A prefix of 0 to 32. Host bits are masked off, and <see cref="Normalised"/> shows the masked form.</description></item>
/// <item><term>any of the above except CIDR, plus <c>:port</c></term><description>Matches only that port (1 to 65535). Without a port, an entry matches every port.</description></item>
/// </list>
/// Names are normalised with <see cref="HostNormaliser"/>. Name entries never match IPv4 hosts, and address entries never match names. No DNS lookups.
/// </remarks>
public sealed class NoProxyRule
{
    private enum RuleKind
    {
        All,
        Domain,
        SubdomainsOnly,
        Address,
        Network,
    }

    private RuleKind _kind;
    private string _name = string.Empty;
    private uint _network;
    private uint _mask;
    private int? _port;

    private NoProxyRule() { }

    /// <summary>The entry as the user typed it, trimmed.</summary>
    public string Original { get; private init; } = string.Empty;

    /// <summary>The canonical form, for display and de-duplication, such as <c>example.com</c> for <c>.Example.COM.</c>.</summary>
    public string Normalised { get; private init; } = string.Empty;

    /// <summary>Parses one entry, or returns false with a short reason the UI can show.</summary>
    public static bool TryParse(string entry, [NotNullWhen(true)] out NoProxyRule? rule, [NotNullWhen(false)] out string? error)
    {
        rule = null;
        var original = (entry ?? string.Empty).Trim();
        if (original.Length == 0)
        {
            error = "The entry is empty.";
            return false;
        }

        if (original == "*")
        {
            rule = new NoProxyRule { Original = original, Normalised = "*", _kind = RuleKind.All };
            error = null;
            return true;
        }

        if (original.Contains('[', StringComparison.Ordinal) || original.Count(c => c == ':') > 1)
        {
            error = "IPv6 isn't supported.";
            return false;
        }

        var slash = original.IndexOf('/', StringComparison.Ordinal);
        if (slash >= 0)
        {
            return TryParseNetwork(original, slash, out rule, out error);
        }

        var hostPart = original;
        int? port = null;
        var colon = original.LastIndexOf(':');
        if (colon >= 0)
        {
            if (!TryParsePort(original[(colon + 1)..], out var parsedPort))
            {
                error = "The port must be a number from 1 to 65535.";
                return false;
            }

            port = parsedPort;
            hostPart = original[..colon];
        }

        var subdomainsOnly = false;
        if (hostPart.StartsWith("*.", StringComparison.Ordinal))
        {
            subdomainsOnly = true;
            hostPart = hostPart[2..];
        }
        else if (hostPart.StartsWith('.'))
        {
            hostPart = hostPart[1..];
        }

        if (hostPart.Contains('*', StringComparison.Ordinal))
        {
            error = "Only a leading *. wildcard is allowed, such as *.example.com.";
            return false;
        }

        if (!HostNormaliser.TryNormalise(hostPart, out var host, out error))
        {
            return false;
        }

        var portSuffix = port is null ? string.Empty : ":" + port.Value.ToString(CultureInfo.InvariantCulture);
        if (host.Kind == HostKind.IPv4)
        {
            if (subdomainsOnly || original.StartsWith('.'))
            {
                error = "Wildcards and leading dots only work with names, not IP addresses.";
                return false;
            }

            rule = new NoProxyRule
            {
                Original = original,
                Normalised = host.Value + portSuffix,
                _kind = RuleKind.Address,
                _network = HostNormaliser.ToUInt32(host.Address!),
                _mask = uint.MaxValue,
                _port = port,
            };
            return true;
        }

        rule = new NoProxyRule
        {
            Original = original,
            Normalised = (subdomainsOnly ? "*." : string.Empty) + host.Value + portSuffix,
            _kind = subdomainsOnly ? RuleKind.SubdomainsOnly : RuleKind.Domain,
            _name = host.Value,
            _port = port,
        };
        return true;
    }

    /// <summary>True when this entry matches the host and port.</summary>
    public bool Matches(NormalisedHost host, int port)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (_port is not null && _port.Value != port)
        {
            return false;
        }

        return _kind switch
        {
            RuleKind.All => true,
            RuleKind.Domain => host.Kind == HostKind.Name && (host.Value == _name || IsSubdomain(host.Value)),
            RuleKind.SubdomainsOnly => host.Kind == HostKind.Name && IsSubdomain(host.Value),
            RuleKind.Address or RuleKind.Network => host.Kind == HostKind.IPv4 && (HostNormaliser.ToUInt32(host.Address!) & _mask) == _network,
            _ => false,
        };
    }

    private bool IsSubdomain(string value) =>
        value.Length > _name.Length + 1
        && value.EndsWith(_name, StringComparison.Ordinal)
        && value[value.Length - _name.Length - 1] == '.';

    private static bool TryParseNetwork(string original, int slash, out NoProxyRule? rule, out string? error)
    {
        rule = null;
        if (original.Contains(':', StringComparison.Ordinal))
        {
            error = "A CIDR range can't have a port.";
            return false;
        }

        var prefixText = original[(slash + 1)..];
        if (prefixText.Length is 0 or > 2 || !prefixText.All(char.IsAsciiDigit)
            || !int.TryParse(prefixText, NumberStyles.None, CultureInfo.InvariantCulture, out var prefix) || prefix > 32)
        {
            error = "The CIDR prefix must be a number from 0 to 32.";
            return false;
        }

        if (!HostNormaliser.TryParseStrictIPv4(original[..slash], out var address))
        {
            error = "A CIDR range needs a plain dotted IPv4 address, such as 10.0.0.0/8.";
            return false;
        }

        var mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
        var network = HostNormaliser.ToUInt32(address) & mask;
        var masked = new IPAddress([(byte)(network >> 24), (byte)(network >> 16), (byte)(network >> 8), (byte)network]);
        rule = new NoProxyRule
        {
            Original = original,
            Normalised = masked + "/" + prefix.ToString(CultureInfo.InvariantCulture),
            _kind = RuleKind.Network,
            _network = network,
            _mask = mask,
        };
        error = null;
        return true;
    }

    private static bool TryParsePort(string text, out int port)
    {
        port = 0;
        return text.Length is >= 1 and <= 5
            && text.All(char.IsAsciiDigit)
            && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out port)
            && port is >= 1 and <= 65535;
    }
}

/// <summary>An immutable, thread-safe bypass list.</summary>
/// <param name="rules">The rules, already parsed.</param>
public sealed class NoProxyList(IReadOnlyList<NoProxyRule> rules)
{
    /// <summary>An empty list.</summary>
    public static NoProxyList Empty { get; } = new([]);

    /// <summary>The rules, in order.</summary>
    public IReadOnlyList<NoProxyRule> Rules { get; } = rules;

    /// <summary>
    /// Parses entries, skipping blank ones. Entries that don't parse are left out and returned in
    /// <paramref name="errors"/> as <c>(entry, reason)</c>, so one bad entry can't disable the rest.
    /// </summary>
    public static NoProxyList Parse(IEnumerable<string> entries, out IReadOnlyList<(string Entry, string Error)> errors)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var rules = new List<NoProxyRule>();
        var problems = new List<(string, string)>();
        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry))
            {
                continue;
            }

            if (NoProxyRule.TryParse(entry, out var rule, out var error))
            {
                rules.Add(rule);
            }
            else
            {
                problems.Add((entry.Trim(), error));
            }
        }

        errors = problems;
        return new NoProxyList(rules);
    }

    /// <summary>Returns the first rule that matches, or null.</summary>
    public NoProxyRule? FindMatch(NormalisedHost host, int port)
    {
        foreach (var rule in Rules)
        {
            if (rule.Matches(host, port))
            {
                return rule;
            }
        }

        return null;
    }
}
