using System.Net;

namespace WSLProxy.Relay.Routing;

/// <summary>What kind of host a request names.</summary>
public enum HostKind
{
    /// <summary>A DNS name, such as <c>example.com</c>.</summary>
    Name,

    /// <summary>A strict dotted-quad IPv4 address, such as <c>10.1.2.3</c>.</summary>
    IPv4,
}

/// <summary>
/// A host from a request target, normalised so the bypass list and the deny list compare like with like.
/// </summary>
/// <param name="Value">The normalised host: lowercase ASCII, international names in punycode, no trailing dot.</param>
/// <param name="Kind">Whether it's a name or an IPv4 address.</param>
/// <param name="Address">The parsed address when <see cref="Kind"/> is <see cref="HostKind.IPv4"/>, otherwise null.</param>
public sealed record NormalisedHost(string Value, HostKind Kind, IPAddress? Address);

/// <summary>Where a request goes.</summary>
public enum RouteKind
{
    /// <summary>Refused with 403: the destination is local, or the request would loop back to the relay.</summary>
    Deny,

    /// <summary>Connect straight to the destination from this machine.</summary>
    Direct,

    /// <summary>Send it to the configured upstream proxy.</summary>
    Upstream,
}

/// <summary>A routing decision.</summary>
/// <param name="Kind">Where the request goes.</param>
/// <param name="Reason">A short reason for logs, such as <c>no_proxy *.corp.example</c> or <c>loopback destination</c>. Never contains a path.</param>
public sealed record RouteDecision(RouteKind Kind, string Reason);
