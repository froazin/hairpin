using System.Globalization;

namespace Hairpin.Engine.Configuration;

/// <summary>
/// Settings for the relay, bound from the <c>Relay</c> configuration section.
/// <c>appsettings.json</c> holds the defaults, and the user's settings file is layered on top.
/// </summary>
public sealed class RelayOptions
{
    /// <summary>The configuration section these options bind from.</summary>
    public const string SectionName = "Relay";

    /// <summary>The TCP port the relay listens on, on 127.0.0.1 only.</summary>
    public int ListenPort { get; set; } = 3128;

    /// <summary>The upstream proxies for HTTP and HTTPS. With no host set, every request goes direct.</summary>
    public UpstreamOptions Upstream { get; set; } = new();

    /// <summary>
    /// Destinations that bypass the upstream proxy and go direct, one entry per item.
    /// See <see cref="Routing.NoProxyRule"/> for the grammar.
    /// </summary>
    /// <remarks>
    /// Keep the default empty. Configuration merges arrays by index, so a default list would
    /// leak its extra entries into a shorter list from the user's settings.
    /// </remarks>
    public IList<string> NoProxy { get; } = [];

    /// <summary>
    /// Refuses loopback, link-local, unspecified, multicast and broadcast destinations, and
    /// <c>localhost</c> names, with 403. On by default.
    /// </summary>
    public bool DenyLocalDestinations { get; set; } = true;

    /// <summary>Size and time limits that protect the relay from misbehaving peers.</summary>
    public RelayLimits Limits { get; set; } = new();

    /// <summary>
    /// Set when the configuration couldn't be bound, for example a port written as text. Validation then rejects these options,
    /// so the relay keeps its last good settings instead of crashing. Never bound from configuration.
    /// </summary>
    public string? ConfigurationError { get; internal set; }
}

/// <summary>
/// The upstream proxies that requests not matched by the bypass list go to, one for <c>http://</c> URLs and one for
/// <c>https://</c> URLs, as <c>HTTP_PROXY</c> and <c>HTTPS_PROXY</c> split them in WSL.
/// </summary>
/// <remarks>
/// Clients send <c>http://</c> URLs as plain requests and tunnel <c>https://</c> URLs with <c>CONNECT</c>, so plain requests
/// use <see cref="ForHttp"/> and tunnels use <see cref="ForHttps"/>. The HTTPS host and port each fall back to the HTTP ones
/// when they're empty, so setting only the HTTP proxy sends everything through it.
/// </remarks>
public sealed class UpstreamOptions
{
    /// <summary>The proxy for <c>http://</c> URLs, like <c>HTTP_PROXY</c>. An empty host sends them direct.</summary>
    public UpstreamProxyOptions Http { get; set; } = new() { Port = 80 };

    /// <summary>The proxy for <c>https://</c> URLs, like <c>HTTPS_PROXY</c>. An empty host or port uses the HTTP one.</summary>
    public UpstreamProxyOptions Https { get; set; } = new();

    /// <summary>How often to probe each upstream proxy with a TCP connect, in seconds. 0 turns probing off.</summary>
    public int HealthCheckIntervalSeconds { get; set; } = 30;

    /// <summary>The proxy plain HTTP requests go to, or null when they go direct.</summary>
    public UpstreamEndpoint? ForHttp => UpstreamEndpoint.From(Http.Host, Http.Port);

    /// <summary>The proxy CONNECT tunnels go to, or null when they go direct: the HTTPS host and port, each defaulting to the HTTP one.</summary>
    public UpstreamEndpoint? ForHttps => UpstreamEndpoint.From(
        string.IsNullOrWhiteSpace(Https.Host) ? Http.Host : Https.Host, Https.Port ?? Http.Port);
}

/// <summary>One upstream proxy's host and port, as set. Either may be empty.</summary>
public sealed class UpstreamProxyOptions
{
    /// <summary>The proxy's host name or IPv4 address.</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>The proxy's port.</summary>
    public int? Port { get; set; }
}

/// <summary>An upstream proxy in use, after the HTTPS proxy's defaults are applied.</summary>
/// <param name="Host">The host name or IPv4 address, trimmed.</param>
/// <param name="Port">The port. 80 when none is set.</param>
public sealed record UpstreamEndpoint(string Host, int Port)
{
    /// <summary>The endpoint for a host and port as set, or null when the host is empty.</summary>
    public static UpstreamEndpoint? From(string? host, int? port) =>
        string.IsNullOrWhiteSpace(host) ? null : new UpstreamEndpoint(host.Trim(), port ?? 80);

    /// <summary>The endpoint as <c>host:port</c>, for messages and the UI.</summary>
    public override string ToString() => Host + ":" + Port.ToString(CultureInfo.InvariantCulture);
}

/// <summary>Size and time limits.</summary>
public sealed class RelayLimits
{
    /// <summary>The longest request line accepted. Longer gets 414.</summary>
    public int MaxRequestLineBytes { get; set; } = 8 * 1024;

    /// <summary>The largest request or response head accepted, request line included. Larger gets 431 (or 502 for a response).</summary>
    public int MaxHeaderBytes { get; set; } = 32 * 1024;

    /// <summary>The most header fields accepted in one head. More gets 431 (or 502 for a response).</summary>
    public int MaxHeaderCount { get; set; } = 100;

    /// <summary>How long a client has to send a complete request head once it starts one.</summary>
    public int HeaderTimeoutSeconds { get; set; } = 15;

    /// <summary>How long to wait for a TCP connection to a destination or the upstream proxy.</summary>
    public int ConnectTimeoutSeconds { get; set; } = 10;

    /// <summary>How long a keep-alive connection may sit idle between requests, or a response may stall.</summary>
    public int IdleTimeoutSeconds { get; set; } = 120;

    /// <summary>How long a CONNECT tunnel may carry no bytes in either direction before it's closed.</summary>
    public int TunnelIdleTimeoutSeconds { get; set; } = 900;

    /// <summary>The most client connections open at once. More are refused with 503.</summary>
    public int MaxConnections { get; set; } = 512;
}
