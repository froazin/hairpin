using Microsoft.Extensions.Logging;
using Hairpin.Engine.Configuration;
using Hairpin.Engine.Routing;

namespace Hairpin.Engine.Proxy;

/// <summary>A failure the relay answers with its own error response.</summary>
internal sealed class RelayException : Exception
{
    public RelayException() { }

    public RelayException(string message) : base(message) { }

    public RelayException(string message, Exception innerException) : base(message, innerException) { }

    public RelayException(int statusCode, string? detail)
        : base(detail ?? statusCode.ToString(System.Globalization.CultureInfo.InvariantCulture))
    {
        StatusCode = statusCode;
        Detail = detail;
    }

    /// <summary>The status code to answer with.</summary>
    public int StatusCode { get; } = 502;

    /// <summary>A relay-written explanation for the error body, or null.</summary>
    public string? Detail { get; }
}

/// <summary>An immutable snapshot of everything a session needs, swapped atomically when the options change.</summary>
/// <param name="Options">The options in force.</param>
/// <param name="Router">The router built from those options and the bound port.</param>
internal sealed record RelayRuntime(RelayOptions Options, Router Router)
{
    /// <summary>The proxy plain HTTP requests go to, or null when they go direct.</summary>
    public UpstreamEndpoint? HttpUpstream { get; } = Options.Upstream.ForHttp;

    /// <summary>The proxy CONNECT tunnels go to, or null when they go direct.</summary>
    public UpstreamEndpoint? HttpsUpstream { get; } = Options.Upstream.ForHttps;

    public RelayLimits Limits => Options.Limits;

    public TimeSpan HeaderTimeout => TimeSpan.FromSeconds(Limits.HeaderTimeoutSeconds);

    public TimeSpan IdleTimeout => TimeSpan.FromSeconds(Limits.IdleTimeoutSeconds);

    public TimeSpan ConnectTimeout => TimeSpan.FromSeconds(Limits.ConnectTimeoutSeconds);

    public TimeSpan TunnelIdleTimeout => TimeSpan.FromSeconds(Limits.TunnelIdleTimeoutSeconds);

    /// <summary>The read buffer for one side of a connection: a whole head plus room for a chunk-size line.</summary>
    public int BufferSize => Limits.MaxHeaderBytes + 8192;
}

/// <summary>What a session reports back to the service that owns it.</summary>
internal interface ISessionHost
{
    RelayRuntime Runtime { get; }

    ILogger Logger { get; }

    void OnRequest();

    void OnUpstreamResult(UpstreamEndpoint upstream, bool reachable);

    TrafficCounters Traffic { get; }
}

/// <summary>Source-generated log messages. None of them carries a path, query, header or body.</summary>
internal static partial class Log
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Relay listening on 127.0.0.1:{Port}")]
    public static partial void Listening(ILogger logger, int port);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Relay couldn't bind its port: {Reason}")]
    public static partial void BindFailed(ILogger logger, string reason);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "Relay paused")]
    public static partial void Paused(ILogger logger);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "Relay stopped")]
    public static partial void Stopped(ILogger logger);

    [LoggerMessage(EventId = 5, Level = LogLevel.Warning, Message = "Ignoring bypass list entry '{Entry}': {Error}")]
    public static partial void BadNoProxyEntry(ILogger logger, string entry, string error);

    [LoggerMessage(EventId = 7, Level = LogLevel.Warning, Message = "Ignored invalid settings: {Problems}")]
    public static partial void InvalidSettings(ILogger logger, string problems);

    [LoggerMessage(EventId = 6, Level = LogLevel.Warning, Message = "Refused a connection: {Limit} connections are already open")]
    public static partial void TooManyConnections(ILogger logger, int limit);

    [LoggerMessage(EventId = 10, Level = LogLevel.Information,
        Message = "{Method} {Host}:{Port} via {Route} ({Reason}) -> {Status}, {RequestBytes} B up, {ResponseBytes} B down, {DurationMs} ms")]
    public static partial void Request(ILogger logger, string method, string host, int port, RouteKind route, string reason,
        int status, long requestBytes, long responseBytes, long durationMs);

    [LoggerMessage(EventId = 11, Level = LogLevel.Information, Message = "Refused request: {Status} ({Reason})")]
    public static partial void Refused(ILogger logger, int status, string reason);

    [LoggerMessage(EventId = 12, Level = LogLevel.Debug, Message = "Connection ended: {Reason}")]
    public static partial void ConnectionEnded(ILogger logger, string reason);

    [LoggerMessage(EventId = 13, Level = LogLevel.Debug, Message = "Opened a server connection to {Host}:{Port} via {Route}")]
    public static partial void ServerOpened(ILogger logger, string host, int port, RouteKind route);

    [LoggerMessage(EventId = 14, Level = LogLevel.Debug, Message = "Reused the server connection to {Host}:{Port}")]
    public static partial void ServerReused(ILogger logger, string host, int port);

    [LoggerMessage(EventId = 15, Level = LogLevel.Debug, Message = "Accepted a connection from client port {ClientPort}")]
    public static partial void Accepted(ILogger logger, int clientPort);

    [LoggerMessage(EventId = 20, Level = LogLevel.Information, Message = "Upstream proxy {Upstream} is {Health}")]
    public static partial void UpstreamHealth(ILogger logger, UpstreamEndpoint upstream, Status.UpstreamHealth health);
}

/// <summary>
/// Wraps a destination stream and records when bytes last went through it, so tunnels and bodies can apply an idle timeout
/// that resets on activity.
/// </summary>
internal sealed class ActivityStream(Stream inner, Action onActivity, Action<int>? onBytes = null) : Stream
{
    public long BytesWritten { get; private set; }

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Flush() => inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count)
    {
        inner.Write(buffer, offset, count);
        BytesWritten += count;
        onBytes?.Invoke(count);
        onActivity();
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        BytesWritten += buffer.Length;
        onBytes?.Invoke(buffer.Length);
        onActivity();
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
}

/// <summary>A cancellation source whose deadline moves forward every time there's activity.</summary>
internal sealed class IdleTimer : IDisposable
{
    private readonly CancellationTokenSource _cts;
    private readonly TimeSpan _timeout;

    public IdleTimer(TimeSpan timeout, CancellationToken parent)
    {
        _timeout = timeout;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(parent);
        _cts.CancelAfter(timeout);
    }

    public CancellationToken Token => _cts.Token;

    /// <summary>True when the timer fired, as opposed to the parent token being cancelled.</summary>
    public bool TimedOut { get; private set; }

    public void Touch()
    {
        try
        {
            _cts.CancelAfter(_timeout);
        }
        catch (ObjectDisposedException)
        {
            // The exchange already finished.
        }
    }

    public void MarkIfTimedOut(CancellationToken parent) => TimedOut = _cts.IsCancellationRequested && !parent.IsCancellationRequested;

    public void Dispose() => _cts.Dispose();
}

/// <summary>
/// Traffic totals since the relay started, updated as bytes are written, so a long tunnel counts while it runs. Bytes are
/// bodies and tunnel data; message heads aren't counted.
/// </summary>
internal sealed class TrafficCounters
{
    private long _bytesUp;
    private long _bytesDown;
    private long _direct;
    private long _upstream;
    private long _errors;

    public void AddUp(int count) => Interlocked.Add(ref _bytesUp, count);

    public void AddDown(int count) => Interlocked.Add(ref _bytesDown, count);

    /// <summary>Counts a request whose response reached the client, by its route.</summary>
    public void Completed(Routing.RouteKind route) => Interlocked.Increment(ref route == Routing.RouteKind.Upstream ? ref _upstream : ref _direct);

    /// <summary>Counts a request the relay answered with its own error, such as a refusal or a failed connection, or whose response broke off.</summary>
    public void Error() => Interlocked.Increment(ref _errors);

    public Status.RelayCounters Read(long requests, int activeConnections) => new(
        requests,
        Interlocked.Read(ref _direct),
        Interlocked.Read(ref _upstream),
        Interlocked.Read(ref _errors),
        Interlocked.Read(ref _bytesUp),
        Interlocked.Read(ref _bytesDown),
        activeConnections);
}
