namespace WSLProxy.Relay.Status;

/// <summary>The relay's lifecycle state.</summary>
public enum RelayState
{
    /// <summary>The relay is starting and hasn't bound its port yet.</summary>
    Starting,

    /// <summary>The relay is listening and accepting connections.</summary>
    Running,

    /// <summary>The user paused the relay: the port is closed and new connections are refused.</summary>
    Paused,

    /// <summary>The relay couldn't start or failed, for example because the port is in use. See <see cref="RelaySnapshot.LastError"/>.</summary>
    Faulted,

    /// <summary>The relay has stopped because the app is shutting down.</summary>
    Stopped,
}

/// <summary>Whether the upstream proxy is reachable.</summary>
public enum UpstreamHealth
{
    /// <summary>No upstream proxy is configured, so every request goes direct.</summary>
    NotConfigured,

    /// <summary>The upstream proxy hasn't been checked yet.</summary>
    Unknown,

    /// <summary>The last connection to the upstream proxy succeeded.</summary>
    Reachable,

    /// <summary>The last connection to the upstream proxy failed.</summary>
    Unreachable,
}

/// <summary>An immutable snapshot of the relay's state, for display.</summary>
/// <param name="State">The lifecycle state.</param>
/// <param name="ListenEndpoint">The endpoint the relay listens on, such as <c>127.0.0.1:3128</c>.</param>
/// <param name="Upstream">The upstream proxy as <c>host:port</c>, or null when every request goes direct.</param>
/// <param name="UpstreamHealth">Whether the upstream proxy is reachable.</param>
/// <param name="ActiveConnections">Client connections open now.</param>
/// <param name="TotalRequests">Requests handled since the relay started, CONNECT tunnels included.</param>
/// <param name="LastError">The most recent error worth showing the user, or null.</param>
public sealed record RelaySnapshot(
    RelayState State,
    string ListenEndpoint,
    string? Upstream,
    UpstreamHealth UpstreamHealth,
    int ActiveConnections,
    long TotalRequests,
    string? LastError);

/// <summary>Read-only view of the relay's state, for the UI.</summary>
public interface IRelayStatus
{
    /// <summary>The current snapshot.</summary>
    RelaySnapshot Current { get; }

    /// <summary>
    /// Raised when the snapshot changes. Raised on a thread-pool thread, and throttled so
    /// connection counters don't raise it more than a few times a second.
    /// </summary>
    event EventHandler<RelaySnapshot>? Changed;
}

/// <summary>Lets the UI pause and resume the relay.</summary>
public interface IRelayControl
{
    /// <summary>Closes the listening port. Open connections finish normally.</summary>
    Task PauseAsync(CancellationToken cancellationToken = default);

    /// <summary>Binds the listening port again.</summary>
    Task ResumeAsync(CancellationToken cancellationToken = default);
}

/// <summary>Traffic totals since the relay started, for the metrics panel. Every value only grows, apart from the open connections.</summary>
/// <param name="Requests">Requests handled, CONNECT tunnels included.</param>
/// <param name="DirectRequests">Requests whose response reached the client on the direct route.</param>
/// <param name="UpstreamRequests">Requests whose response reached the client through the upstream proxy.</param>
/// <param name="Errors">Requests the relay answered with its own error, such as a refusal, a failed connection or a timeout, or whose response broke off part way.</param>
/// <param name="BytesUp">Bytes sent towards servers: request bodies and tunnel data, not message heads.</param>
/// <param name="BytesDown">Bytes sent back to clients: response bodies and tunnel data, not message heads.</param>
/// <param name="ActiveConnections">Client connections open now.</param>
public sealed record RelayCounters(
    long Requests,
    long DirectRequests,
    long UpstreamRequests,
    long Errors,
    long BytesUp,
    long BytesDown,
    int ActiveConnections);

/// <summary>Reads the relay's traffic totals. Cheap and lock-free, so the UI can poll it every second.</summary>
public interface IRelayMetrics
{
    /// <summary>The totals now.</summary>
    RelayCounters Read();
}
