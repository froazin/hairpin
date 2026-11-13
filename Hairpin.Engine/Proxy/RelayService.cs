using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Hairpin.Engine.Configuration;
using Hairpin.Engine.Http;
using Hairpin.Engine.Routing;
using Hairpin.Engine.Status;

namespace Hairpin.Engine.Proxy;

/// <summary>
/// The relay: owns the listening socket on 127.0.0.1, accepts client connections and runs a <see cref="ClientSession"/> for each.
/// Applies option changes live, probes the upstream proxy, and publishes its state for the UI.
/// </summary>
public sealed partial class RelayService : BackgroundService, IRelayStatus, IRelayControl, IRelayMetrics, ISessionHost
{
    private static readonly TimeSpan BindRetryDelay = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan SessionDrainTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PublishInterval = TimeSpan.FromMilliseconds(250);
    private const int MaxConsecutiveAcceptFailures = 20;

    private readonly IOptionsMonitor<RelayOptions> _options;
    private readonly ILogger<RelayService> _logger;
    private readonly RelayOptionsValidator _validator = new();
    private readonly Lock _gate = new();
    private readonly Lock _deliveryGate = new();
    private readonly SemaphoreSlim _wake = new(0);
    private readonly CancellationTokenSource _sessionsCts = new();
    private readonly ConcurrentDictionary<long, Task> _sessions = new();

    private RelayRuntime _runtime;
    private IDisposable? _onChange;
    private CancellationTokenSource? _listenCts;
    private bool _paused;
    private bool _hasValidOptions;
    private int _boundPort;
    private int _active;
    private string? _appliedFingerprint;
    private readonly TrafficCounters _traffic = new();
    private long _total;
    private long _sessionIds;
    private int _publishScheduled;
    private RelayState _state = RelayState.Starting;
    private string? _lastError;
    private string? _optionsError;
    private Dictionary<UpstreamEndpoint, UpstreamHealth> _health = [];

    /// <summary>Creates the relay. Use <see cref="ServiceCollectionExtensions.AddRelay"/> rather than calling this directly.</summary>
    public RelayService(IOptionsMonitor<RelayOptions> options, ILogger<RelayService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _logger = logger;
        var initial = options.CurrentValue;
        _runtime = BuildRuntime(initial, initial.ListenPort);
        _health = HealthFor(_runtime, []);
    }

    /// <inheritdoc />
    public event EventHandler<RelaySnapshot>? Changed;

    /// <inheritdoc />
    public RelaySnapshot Current
    {
        get
        {
            lock (_gate)
            {
                var options = _runtime.Options;
                var port = _boundPort != 0 ? _boundPort : options.ListenPort;
                return new RelaySnapshot(
                    _state,
                    "127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture),
                    StatusOf(_runtime.HttpUpstream),
                    StatusOf(_runtime.HttpsUpstream),
                    Volatile.Read(ref _active),
                    Interlocked.Read(ref _total),
                    _optionsError ?? _lastError);
            }
        }
    }

    RelayRuntime ISessionHost.Runtime => Volatile.Read(ref _runtime);

    ILogger ISessionHost.Logger => _logger;

    void ISessionHost.OnRequest()
    {
        Interlocked.Increment(ref _total);
        Publish(immediate: false);
    }

    void ISessionHost.OnUpstreamResult(UpstreamEndpoint upstream, bool reachable) => SetUpstreamHealth(upstream, reachable ? UpstreamHealth.Reachable : UpstreamHealth.Unreachable);

    TrafficCounters ISessionHost.Traffic => _traffic;

    /// <inheritdoc />
    public RelayCounters Read() => _traffic.Read(Interlocked.Read(ref _total), Volatile.Read(ref _active));

    /// <inheritdoc />
    public Task PauseAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_paused)
            {
                return Task.CompletedTask;
            }

            _paused = true;
            _state = RelayState.Paused;
            _lastError = null;
            _listenCts?.Cancel();
        }

        Log.Paused(_logger);
        _wake.Release();
        Publish(immediate: true);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ResumeAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_paused)
            {
                return Task.CompletedTask;
            }

            _paused = false;
            _state = RelayState.Starting;
        }

        _wake.Release();
        Publish(immediate: true);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        _onChange?.Dispose();
        _sessionsCts.Dispose();
        _wake.Dispose();
        base.Dispose();
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _onChange = _options.OnChange(ApplyOptions);
        ApplyOptions(_options.CurrentValue);
        var health = HealthLoopAsync(stoppingToken);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                bool canListen;
                lock (_gate)
                {
                    canListen = !_paused && _hasValidOptions;
                }

                if (!canListen)
                {
                    await _wake.WaitAsync(Timeout.InfiniteTimeSpan, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                var port = Volatile.Read(ref _runtime).Options.ListenPort;
                Socket listener;
                try
                {
                    listener = Bind(port);
                }
                catch (SocketException ex)
                {
                    var reason = DescribeBindFailure(ex, port);
                    Log.BindFailed(_logger, reason);
                    SetState(RelayState.Faulted, reason);
                    await _wake.WaitAsync(BindRetryDelay, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                using var listenCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                try
                {
                    var bound = ((IPEndPoint)listener.LocalEndpoint()).Port;
                    lock (_gate)
                    {
                        // The settings may have changed, or a pause arrived, while we were binding. Start again if so.
                        if (_paused || _runtime.Options.ListenPort != port)
                        {
                            continue;
                        }

                        _listenCts = listenCts;
                        _boundPort = bound;
                        _runtime = BuildRuntime(_runtime.Options, bound);
                    }

                    Log.Listening(_logger, bound);
                    SetState(RelayState.Running, null);
                    await AcceptLoopAsync(listener, listenCts.Token).ConfigureAwait(false);
                }
                finally
                {
                    listener.Dispose();
                    lock (_gate)
                    {
                        _listenCts = null;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
        finally
        {
            await _sessionsCts.CancelAsync().ConfigureAwait(false);
            await Task.WhenAny(Task.WhenAll(_sessions.Values), Task.Delay(SessionDrainTimeout, CancellationToken.None)).ConfigureAwait(false);
            try
            {
                await health.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Expected on shutdown (cancellation); anything else has already stopped the probes and mustn't stop shutdown.
            }

            SetState(RelayState.Stopped, null);
            Log.Stopped(_logger);
        }
    }

    private async Task AcceptLoopAsync(Socket listener, CancellationToken cancellationToken)
    {
        var failures = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await listener.AcceptAsync(cancellationToken).ConfigureAwait(false);
                failures = 0;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionAborted)
            {
                // A client that gave up before we accepted it. Keep listening.
                continue;
            }
            catch (SocketException ex)
            {
                // Something persistent, such as running out of handles. Back off, and rebind if it doesn't clear.
                Log.BindFailed(_logger, "accept failed: " + ex.SocketErrorCode);
                if (++failures >= MaxConsecutiveAcceptFailures)
                {
                    return;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(100 * failures), cancellationToken).ConfigureAwait(false);
                continue;
            }

            var limit = Volatile.Read(ref _runtime).Limits.MaxConnections;
            if (Interlocked.Increment(ref _active) > limit)
            {
                Interlocked.Decrement(ref _active);
                Log.TooManyConnections(_logger, limit);
                _ = RejectAsync(client);
                continue;
            }

            var id = Interlocked.Increment(ref _sessionIds);
            var task = Task.Run(() => RunSessionAsync(client), CancellationToken.None);
            _sessions[id] = task;
            _ = task.ContinueWith(_ => _sessions.TryRemove(id, out var _), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            Publish(immediate: false);
        }
    }

    private async Task RunSessionAsync(Socket client)
    {
        try
        {
            client.NoDelay = true;
            if (_logger.IsEnabled(LogLevel.Debug) && client.RemoteEndPoint is System.Net.IPEndPoint remote)
            {
                Log.Accepted(_logger, remote.Port);
            }

            var session = new ClientSession(client, this);
            await using (session.ConfigureAwait(false))
            {
                await session.RunAsync(_sessionsCts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // The relay is stopping.
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or HttpFramingException)
        {
            Log.ConnectionEnded(_logger, ex is HttpFramingException ? "malformed body" : "connection reset or closed");
        }
        catch (Exception ex)
        {
            // Never let one connection take the process down. Log the type only: messages can carry peer data.
            LogUnexpected(_logger, ex.GetType().FullName ?? nameof(Exception));
        }
        finally
        {
            try
            {
                client.Shutdown(SocketShutdown.Both);
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                // Already closed.
            }

            client.Dispose();
            Interlocked.Decrement(ref _active);
            Publish(immediate: false);
        }
    }

    private static async Task RejectAsync(Socket client)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await client.SendAsync(HeadWriter.BuildError(503), SocketFlags.None, cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ObjectDisposedException)
        {
            // Best effort.
        }
        finally
        {
            client.Dispose();
        }
    }

    private async Task HealthLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var runtime = Volatile.Read(ref _runtime);
            var interval = runtime.Options.Upstream.HealthCheckIntervalSeconds;
            var upstreams = UpstreamsOf(runtime);
            if (upstreams.Count == 0 || interval <= 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                continue;
            }

            foreach (var upstream in upstreams)
            {
                try
                {
                    using var probe = await Connector.ConnectUpstreamAsync(upstream, runtime.Router.ListenPort, runtime.ConnectTimeout, cancellationToken)
                        .ConfigureAwait(false);
                    SetUpstreamHealth(upstream, UpstreamHealth.Reachable);
                }
                catch (RelayException)
                {
                    SetUpstreamHealth(upstream, UpstreamHealth.Unreachable);
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(interval), cancellationToken).ConfigureAwait(false);
        }
    }

    private void ApplyOptions(RelayOptions options)
    {
        var validation = _validator.Validate(null, options);
        if (validation.Failed)
        {
            // Keep the last good settings running, and say why the new ones were ignored.
            bool hadValid;
            lock (_gate)
            {
                hadValid = _hasValidOptions;
                _optionsError = (hadValid
                    ? "The settings are invalid, so the previous ones are still in use: "
                    : "The settings are invalid, so the relay can't start: ") + string.Join(" ", validation.Failures);
                if (!hadValid)
                {
                    _state = RelayState.Faulted;
                }
            }

            Log.InvalidSettings(_logger, string.Join(" ", validation.Failures));
            Publish(immediate: true);
            return;
        }

        // Any configuration reload, such as the tray's debug logging switch, re-delivers the options. Keep the runtime when
        // nothing changed, so kept-alive server connections stay usable and bad bypass entries aren't logged again.
        var fingerprint = System.Text.Json.JsonSerializer.Serialize(options);
        lock (_gate)
        {
            if (_hasValidOptions && _optionsError is null && fingerprint == _appliedFingerprint)
            {
                return;
            }

            _appliedFingerprint = fingerprint;
        }

        NoProxyList.Parse(options.NoProxy, out var errors);
        foreach (var (entry, error) in errors)
        {
            Log.BadNoProxyEntry(_logger, entry, error);
        }

        bool wake;
        lock (_gate)
        {
            var previous = _runtime.Options;
            wake = !_hasValidOptions || previous.ListenPort != options.ListenPort;
            _hasValidOptions = true;
            _optionsError = null;
            if (previous.ListenPort != options.ListenPort)
            {
                _boundPort = 0;
                _listenCts?.Cancel();
            }

            var routerPort = options.ListenPort != 0 ? options.ListenPort : _boundPort;
            _runtime = BuildRuntime(options, routerPort);
            _health = HealthFor(_runtime, _health);
        }

        if (wake)
        {
            _wake.Release();
        }

        Publish(immediate: true);
    }

    private static RelayRuntime BuildRuntime(RelayOptions options, int listenPort)
    {
        var list = NoProxyList.Parse(options.NoProxy, out _);
        return new RelayRuntime(options, new Router(list, options.Upstream.ForHttp is not null, options.Upstream.ForHttps is not null,
            listenPort, options.DenyLocalDestinations));
    }

    /// <summary>The distinct upstream proxies a runtime uses: none, one, or two when HTTPS has its own.</summary>
    private static List<UpstreamEndpoint> UpstreamsOf(RelayRuntime runtime) =>
        [.. new[] { runtime.HttpUpstream, runtime.HttpsUpstream }.OfType<UpstreamEndpoint>().Distinct()];

    /// <summary>The health of each upstream a runtime uses, kept from <paramref name="previous"/> for any that didn't change.</summary>
    private static Dictionary<UpstreamEndpoint, UpstreamHealth> HealthFor(RelayRuntime runtime, Dictionary<UpstreamEndpoint, UpstreamHealth> previous) =>
        UpstreamsOf(runtime).ToDictionary(u => u, u => previous.GetValueOrDefault(u, UpstreamHealth.Unknown));

    /// <summary>The status of one upstream, for the snapshot. Call under <see cref="_gate"/>.</summary>
    private UpstreamStatus? StatusOf(UpstreamEndpoint? upstream) =>
        upstream is null ? null : new UpstreamStatus(upstream.ToString(), _health.GetValueOrDefault(upstream, UpstreamHealth.Unknown));

    private static Socket Bind(int port)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            // Must be set before Bind: stops another process taking over the port while we hold it.
            socket.ExclusiveAddressUse = true;
            socket.Bind(new IPEndPoint(IPAddress.Loopback, port));
            socket.Listen(512);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static string DescribeBindFailure(SocketException ex, int port)
    {
        var p = port.ToString(CultureInfo.InvariantCulture);
        return ex.SocketErrorCode switch
        {
            SocketError.AddressAlreadyInUse =>
                $"Port {p} is already in use by another program, possibly a Linux process in WSL (mirrored mode shares ports).",
            SocketError.AccessDenied =>
                $"Port {p} is reserved or blocked. Check: netsh int ipv4 show excludedportrange protocol=tcp",
            _ => $"Port {p} couldn't be opened ({ex.SocketErrorCode}).",
        };
    }

    private void SetState(RelayState state, string? error)
    {
        lock (_gate)
        {
            if (_paused && state is RelayState.Running or RelayState.Faulted)
            {
                state = RelayState.Paused;
            }

            _state = state;
            _lastError = error;
        }

        Publish(immediate: true);
    }

    private void SetUpstreamHealth(UpstreamEndpoint upstream, UpstreamHealth health)
    {
        lock (_gate)
        {
            // A session or probe can finish after the settings changed: ignore results for upstreams no longer in use.
            if (!_health.TryGetValue(upstream, out var current) || current == health)
            {
                return;
            }

            _health[upstream] = health;
        }

        Log.UpstreamHealth(_logger, upstream, health);
        Publish(immediate: true);
    }

    private void Publish(bool immediate)
    {
        if (immediate)
        {
            ThreadPool.QueueUserWorkItem(_ => Raise());
            return;
        }

        if (Interlocked.CompareExchange(ref _publishScheduled, 1, 0) == 0)
        {
            _ = Task.Delay(PublishInterval, CancellationToken.None).ContinueWith(
                _ =>
                {
                    Volatile.Write(ref _publishScheduled, 0);
                    Raise();
                },
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);
        }
    }

    private void Raise()
    {
        // Read the snapshot and deliver it under one lock, so a slower delivery can never overwrite a newer state.
        lock (_deliveryGate)
        {
            var handler = Changed;
            if (handler is null)
            {
                return;
            }

            try
            {
                handler(this, Current);
            }
            catch (Exception ex)
            {
                // A UI handler bug mustn't stop the relay.
                LogUnexpected(_logger, ex.GetType().FullName ?? nameof(Exception));
            }
        }
    }

    [LoggerMessage(EventId = 30, Level = LogLevel.Error, Message = "Unexpected error: {ExceptionType}")]
    private static partial void LogUnexpected(ILogger logger, string exceptionType);
}

/// <summary>Small socket helpers.</summary>
internal static class SocketExtensions
{
    /// <summary>The bound local endpoint. Throws if the socket isn't bound, which can't happen after a successful Bind.</summary>
    public static EndPoint LocalEndpoint(this Socket socket) =>
        socket.LocalEndPoint ?? throw new InvalidOperationException("The listener isn't bound.");
}
