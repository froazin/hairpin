namespace WSLProxy.Relay.Configuration;

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

    /// <summary>The upstream proxy. When its host is empty, every request goes direct.</summary>
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

/// <summary>The upstream proxy that requests not matched by the bypass list go to.</summary>
public sealed class UpstreamOptions
{
    /// <summary>The upstream proxy's host name or IPv4 address. Empty means no upstream.</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>The upstream proxy's port.</summary>
    public int Port { get; set; } = 80;

    /// <summary>How often to probe the upstream proxy with a TCP connect, in seconds. 0 turns probing off.</summary>
    public int HealthCheckIntervalSeconds { get; set; } = 30;

    /// <summary>True when an upstream proxy is configured.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host);
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
