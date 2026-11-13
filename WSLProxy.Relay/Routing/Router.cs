using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;

namespace WSLProxy.Relay.Routing;

/// <summary>Decides which destinations are refused. Pure and thread-safe.</summary>
/// <remarks>
/// When <c>denyLocal</c> is on, these are refused: names <c>localhost</c> and <c>*.localhost</c> (RFC 6761); addresses in
/// <c>0.0.0.0/8</c>, <c>127.0.0.0/8</c>, <c>169.254.0.0/16</c> (link-local, including cloud metadata at <c>169.254.169.254</c>),
/// <c>224.0.0.0/4</c> (multicast), <c>255.255.255.255</c> and the Azure platform endpoint <c>168.63.129.16</c>.
/// The relay's own listener is always refused, whatever the setting, to stop loops. Any address that isn't IPv4 is always
/// refused, because the relay is IPv4 only. RFC 1918 private ranges are never refused: intranet hosts must stay reachable.
/// </remarks>
public static class DestinationPolicy
{
    private static readonly uint AzureWireServer = 0xA83F8110; // 168.63.129.16

    /// <summary>True, with a reason, when a request to this host must be refused before any DNS lookup.</summary>
    public static bool IsDeniedHost(NormalisedHost host, int port, int listenPort, bool denyLocal, [NotNullWhen(true)] out string? reason)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (host.Kind == HostKind.IPv4)
        {
            return IsDeniedAddress(host.Address!, port, listenPort, denyLocal, out reason);
        }

        var isLocalhost = host.Value == "localhost" || host.Value.EndsWith(".localhost", StringComparison.Ordinal);
        if (isLocalhost && port == listenPort)
        {
            reason = "the relay itself";
            return true;
        }

        if (isLocalhost && denyLocal)
        {
            reason = "localhost destination";
            return true;
        }

        reason = null;
        return false;
    }

    /// <summary>
    /// True, with a reason, when a resolved address must not be connected to. Used on the direct path after DNS,
    /// so a public name that resolves to a local address can't get through (DNS rebinding).
    /// </summary>
    public static bool IsDeniedAddress(IPAddress address, int port, int listenPort, bool denyLocal, [NotNullWhen(true)] out string? reason)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            reason = "IPv6 destination";
            return true;
        }

        var value = HostNormaliser.ToUInt32(address);
        var isLoopback = (value >> 24) == 127;
        var isThisNetwork = (value >> 24) == 0;
        if ((isLoopback || isThisNetwork) && port == listenPort)
        {
            reason = "the relay itself";
            return true;
        }

        if (!denyLocal)
        {
            reason = null;
            return false;
        }

        reason = value switch
        {
            _ when isThisNetwork => "unspecified destination",
            _ when isLoopback => "loopback destination",
            _ when (value >> 16) == 0xA9FE => "link-local destination",
            _ when (value >> 28) == 0xE => "multicast destination",
            uint.MaxValue => "broadcast destination",
            _ when value == AzureWireServer => "platform endpoint",
            _ => null,
        };
        return reason is not null;
    }
}

/// <summary>Combines the deny list, the bypass list and the upstream setting into one decision. Pure and thread-safe.</summary>
public sealed class Router
{
    /// <summary>Creates a router.</summary>
    /// <param name="noProxy">The bypass list.</param>
    /// <param name="hasUpstream">True when an upstream proxy is configured.</param>
    /// <param name="listenPort">The relay's own port, to refuse loops.</param>
    /// <param name="denyLocal">Whether local destinations are refused.</param>
    public Router(NoProxyList noProxy, bool hasUpstream, int listenPort, bool denyLocal)
    {
        NoProxy = noProxy;
        HasUpstream = hasUpstream;
        ListenPort = listenPort;
        DenyLocal = denyLocal;
    }

    /// <summary>The bypass list.</summary>
    public NoProxyList NoProxy { get; }

    /// <summary>True when an upstream proxy is configured.</summary>
    public bool HasUpstream { get; }

    /// <summary>The relay's own port.</summary>
    public int ListenPort { get; }

    /// <summary>Whether local destinations are refused.</summary>
    public bool DenyLocal { get; }

    /// <summary>
    /// Decides the route: <see cref="RouteKind.Deny"/> if <see cref="DestinationPolicy.IsDeniedHost"/> says so;
    /// otherwise <see cref="RouteKind.Direct"/> if there's no upstream or the bypass list matches;
    /// otherwise <see cref="RouteKind.Upstream"/>.
    /// </summary>
    public RouteDecision Decide(NormalisedHost host, int port)
    {
        if (DestinationPolicy.IsDeniedHost(host, port, ListenPort, DenyLocal, out var reason))
        {
            return new RouteDecision(RouteKind.Deny, reason);
        }

        if (!HasUpstream)
        {
            return new RouteDecision(RouteKind.Direct, "no upstream");
        }

        var match = NoProxy.FindMatch(host, port);
        return match is null
            ? new RouteDecision(RouteKind.Upstream, "upstream")
            : new RouteDecision(RouteKind.Direct, "no_proxy " + match.Normalised);
    }
}
