using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;

namespace Hairpin.Engine.Routing;

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
/// <param name="noProxy">The bypass list.</param>
/// <param name="hasHttpUpstream">True when plain HTTP requests have an upstream proxy.</param>
/// <param name="hasHttpsUpstream">True when CONNECT tunnels have an upstream proxy.</param>
/// <param name="listenPort">The relay's own port, to refuse loops.</param>
/// <param name="denyLocal">Whether local destinations are refused.</param>
public sealed class Router(NoProxyList noProxy, bool hasHttpUpstream, bool hasHttpsUpstream, int listenPort, bool denyLocal)
{
    /// <summary>The bypass list.</summary>
    public NoProxyList NoProxy { get; } = noProxy;

    /// <summary>True when plain HTTP requests have an upstream proxy.</summary>
    public bool HasHttpUpstream { get; } = hasHttpUpstream;

    /// <summary>True when CONNECT tunnels have an upstream proxy.</summary>
    public bool HasHttpsUpstream { get; } = hasHttpsUpstream;

    /// <summary>The relay's own port.</summary>
    public int ListenPort { get; } = listenPort;

    /// <summary>Whether local destinations are refused.</summary>
    public bool DenyLocal { get; } = denyLocal;

    /// <summary>
    /// Decides the route: <see cref="RouteKind.Deny"/> if <see cref="DestinationPolicy.IsDeniedHost"/> says so;
    /// otherwise <see cref="RouteKind.Direct"/> if the request's kind has no upstream or the bypass list matches;
    /// otherwise <see cref="RouteKind.Upstream"/>.
    /// </summary>
    /// <param name="host">The destination host.</param>
    /// <param name="port">The destination port.</param>
    /// <param name="tunnel">True for a CONNECT tunnel, which uses the HTTPS upstream; false for a plain HTTP request.</param>
    public RouteDecision Decide(NormalisedHost host, int port, bool tunnel)
    {
        if (DestinationPolicy.IsDeniedHost(host, port, ListenPort, DenyLocal, out var reason))
        {
            return new RouteDecision(RouteKind.Deny, reason);
        }

        if (!(tunnel ? HasHttpsUpstream : HasHttpUpstream))
        {
            return new RouteDecision(RouteKind.Direct, "no upstream");
        }

        var match = NoProxy.FindMatch(host, port);
        return match is null
            ? new RouteDecision(RouteKind.Upstream, "upstream")
            : new RouteDecision(RouteKind.Direct, "no_proxy " + match.Normalised);
    }
}
