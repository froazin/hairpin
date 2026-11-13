using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using WSLProxy.Relay.Configuration;
using WSLProxy.Relay.Http;
using WSLProxy.Relay.Routing;

namespace WSLProxy.Relay.Proxy;

/// <summary>
/// Serves one client connection: a keep-alive loop of plain HTTP requests, or one CONNECT tunnel. Server connections are kept
/// per session only, never shared between clients, so a misbehaving server can't desync another client.
/// </summary>
internal sealed class ClientSession : IAsyncDisposable
{
    private static readonly byte[] ConnectEstablished = "HTTP/1.1 200 Connection established\r\n\r\n"u8.ToArray();

    /// <summary>Methods RFC 9110 section 9.2.2 calls idempotent, which a proxy may retry. TRACE is refused anyway.</summary>
    private static readonly HashSet<string> IdempotentMethods = new(StringComparer.Ordinal) { "GET", "HEAD", "OPTIONS", "PUT", "DELETE" };

    private readonly Socket _client;
    private readonly NetworkStream _clientStream;
    private readonly ISessionHost _host;
    private readonly HttpReader _clientReader;
    private ServerConnection? _server;

    public ClientSession(Socket client, ISessionHost host)
    {
        _client = client;
        _host = host;
        _clientStream = new NetworkStream(client, ownsSocket: false);
        _clientReader = new HttpReader(_clientStream, host.Runtime.BufferSize);
    }

    /// <summary>Why reading a response head produced no usable head.</summary>
    private enum HeadOutcome
    {
        Ok,

        /// <summary>The server closed or reset the connection before sending any byte of a head.</summary>
        Closed,

        /// <summary>No complete head arrived within the idle timeout. The server may have acted on the request.</summary>
        TimedOut,

        /// <summary>A head arrived but was cut short, too large or malformed.</summary>
        Invalid,
    }

    private ILogger Logger => _host.Logger;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var first = true;
        while (!cancellationToken.IsCancellationRequested)
        {
            // Take a fresh snapshot per request, so option changes apply to the next request on a kept-alive connection.
            var runtime = _host.Runtime;
            HeadReadResult read;
            using (var headCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                headCts.CancelAfter(first ? runtime.HeaderTimeout : runtime.IdleTimeout);
                try
                {
                    read = await _clientReader.ReadHeadAsync(runtime.Limits.MaxHeaderBytes, headCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    Log.ConnectionEnded(Logger, first ? "no request head in time" : "idle");
                    return;
                }
            }

            first = false;
            switch (read.Status)
            {
                case HeadReadStatus.EndOfStream:
                    return;
                case HeadReadStatus.Truncated:
                    Log.ConnectionEnded(Logger, "client closed part way through a request head");
                    return;
                case HeadReadStatus.TooLarge:
                    await RefuseAsync(431, "request head too large", null, cancellationToken).ConfigureAwait(false);
                    return;
            }

            if (!HeadParser.TryParseRequest(read.Head.Span, runtime.Limits.MaxRequestLineBytes, runtime.Limits.MaxHeaderCount, out var request, out var error))
            {
                await RefuseAsync(HeadWriter.StatusFor(error, isRequest: true), "invalid request: " + error, null, cancellationToken).ConfigureAwait(false);
                return;
            }

            _host.OnRequest();
            if (request.Headers.Any(f => Framing.IsNamed(f, "Via") && f.Value.Contains("wsl-proxy-relay", StringComparison.OrdinalIgnoreCase)))
            {
                // The request has been through this relay already, for example via an upstream that points back here.
                await RefuseAsync(403, "loop detected", "The request has already passed through this relay.", cancellationToken).ConfigureAwait(false);
                return;
            }

            if (!HostNormaliser.TryNormalise(request.Host, out var host, out var hostError))
            {
                await RefuseAsync(400, "invalid host", hostError, cancellationToken).ConfigureAwait(false);
                return;
            }

            var decision = runtime.Router.Decide(host, request.Port, tunnel: request.Form == TargetForm.Authority);
            if (decision.Kind == RouteKind.Deny)
            {
                await RefuseAsync(403, decision.Reason, null, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (request.Form == TargetForm.Authority)
            {
                await HandleConnectAsync(runtime, request, host, decision, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (!await HandleHttpAsync(runtime, request, host, decision, cancellationToken).ConfigureAwait(false))
            {
                return;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_server is not null)
        {
            await _server.DisposeAsync().ConfigureAwait(false);
        }

        _clientReader.Dispose();
        await _clientStream.DisposeAsync().ConfigureAwait(false);
    }

    private async Task HandleConnectAsync(RelayRuntime runtime, RequestHead request, NormalisedHost host, RouteDecision decision, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        Socket socket;
        try
        {
            socket = decision.Kind == RouteKind.Upstream
                ? await ConnectUpstreamAsync(runtime, runtime.HttpsUpstream!, cancellationToken).ConfigureAwait(false)
                : await Connector.ConnectDirectAsync(host, request.Port, runtime.Router, runtime.ConnectTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (RelayException ex)
        {
            await RefuseAsync(ex.StatusCode, decision.Reason, ex.Detail, cancellationToken).ConfigureAwait(false);
            return;
        }

        await using var server = new ServerConnection(socket, runtime, string.Empty);
        Log.ServerOpened(Logger, host.Value, request.Port, decision.Kind);
        var status = 200;
        if (decision.Kind == RouteKind.Upstream)
        {
            // Ask the upstream proxy for the tunnel and wait for its answer before forwarding any early client bytes,
            // so the upstream never parses tunnel data as an HTTP request.
            ResponseHead? reply;
            HeadOutcome outcome;
            try
            {
                await server.Stream.WriteAsync(HeadWriter.BuildConnect(request.Host, request.Port), cancellationToken).ConfigureAwait(false);
                using var idle = new IdleTimer(runtime.IdleTimeout, cancellationToken);
                (reply, outcome) = await ReadResponseHeadAsync(runtime, server, idle.Token, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or SocketException)
            {
                (reply, outcome) = (null, HeadOutcome.Closed);
            }

            if (reply is null)
            {
                var detail = outcome == HeadOutcome.TimedOut
                    ? "The upstream proxy didn't answer the tunnel request in time."
                    : "The upstream proxy closed the connection or sent an invalid response.";
                await RefuseAsync(outcome == HeadOutcome.TimedOut ? 504 : 502, decision.Reason, detail, cancellationToken).ConfigureAwait(false);
                return;
            }

            status = reply.StatusCode;
            if (reply.StatusCode is < 200 or > 299)
            {
                // Pass the upstream's refusal (for example 403 or 407) through, then close.
                if (!Framing.TryGetResponseFraming("CONNECT", reply, out var framing, out _))
                {
                    await RefuseAsync(502, decision.Reason, "The upstream proxy sent an invalid response.", cancellationToken).ConfigureAwait(false);
                    return;
                }

                long passed = 0;
                try
                {
                    await _clientStream.WriteAsync(HeadWriter.BuildResponse(reply, close: true), cancellationToken).ConfigureAwait(false);
                    passed = await CopyWithIdleAsync(server.Reader, framing, _clientStream, runtime, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or SocketException or HttpFramingException)
                {
                    // The refusal has been sent as far as it could be; the connection closes either way.
                }

                LogRequest(request, decision, status, 0, passed, started);
                return;
            }
        }

        await _clientStream.WriteAsync(ConnectEstablished, cancellationToken).ConfigureAwait(false);
        var (up, down) = await Tunnel.RunAsync(
            new Tunnel.Side(_client, _clientStream, _clientReader),
            new Tunnel.Side(server.Socket, server.Stream, server.Reader),
            runtime.TunnelIdleTimeout,
            cancellationToken,
            _host.Traffic.AddUp,
            _host.Traffic.AddDown).ConfigureAwait(false);
        LogRequest(request, decision, status, up, down, started);
    }

    /// <summary>Handles one plain HTTP exchange. Returns true when the client connection can carry another request.</summary>
    private async Task<bool> HandleHttpAsync(RelayRuntime runtime, RequestHead request, NormalisedHost host, RouteDecision decision, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        if (!Framing.TryGetRequestFraming(request, out var requestFraming, out var framingError))
        {
            await RefuseAsync(HeadWriter.StatusFor(framingError, isRequest: true), "invalid request framing: " + framingError, null, cancellationToken).ConfigureAwait(false);
            return false;
        }

        var clientWantsClose = Framing.WantsClose(request.MinorVersion, request.Headers);
        var toUpstream = decision.Kind == RouteKind.Upstream;
        var key = toUpstream ? "upstream" : "direct:" + host.Value + ":" + request.Port.ToString(CultureInfo.InvariantCulture);
        var head = HeadWriter.BuildRequest(request, toUpstream, clientWantsClose);

        // A kept-alive server connection may have been closed by the server while idle. When it turns out closed before any
        // response byte, retry once on a fresh connection, but only for an idempotent request with no body (RFC 9110 9.2.2).
        var canRetry = requestFraming.Kind == BodyKind.None && IdempotentMethods.Contains(request.Method);
        for (var attempt = 0; ; attempt++)
        {
            bool reused;
            try
            {
                reused = await EnsureServerAsync(runtime, key, host, request.Port, toUpstream, cancellationToken).ConfigureAwait(false);
            }
            catch (RelayException ex)
            {
                await RefuseAsync(ex.StatusCode, decision.Reason, ex.Detail, cancellationToken).ConfigureAwait(false);
                return false;
            }

            var retryAllowed = reused && canRetry && attempt == 0;
            var server = _server!;
            try
            {
                await server.Stream.WriteAsync(head, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or SocketException)
            {
                await DropServerAsync().ConfigureAwait(false);
                if (retryAllowed)
                {
                    continue;
                }

                await RefuseAsync(502, decision.Reason, "The connection to the server failed.", cancellationToken).ConfigureAwait(false);
                return false;
            }

            var outcome = await ExchangeAsync(runtime, request, requestFraming, clientWantsClose, server, cancellationToken).ConfigureAwait(false);
            if (outcome.NoResponse && outcome.ClosedEarly && retryAllowed)
            {
                await DropServerAsync().ConfigureAwait(false);
                continue;
            }

            if (outcome.NoResponse)
            {
                await DropServerAsync().ConfigureAwait(false);
                await RefuseAsync(outcome.ErrorStatus, decision.Reason, outcome.ErrorDetail, cancellationToken).ConfigureAwait(false);
                LogRequest(request, decision, outcome.ErrorStatus, outcome.RequestBytes, 0, started, CountAs.AlreadyCounted);
                return false;
            }

            if (outcome.DropServer)
            {
                await DropServerAsync().ConfigureAwait(false);
            }

            LogRequest(request, decision, outcome.Status, outcome.RequestBytes, outcome.ResponseBytes, started,
                outcome.BrokeOff ? CountAs.Error : CountAs.Completed);
            return !outcome.CloseClient;
        }
    }

    /// <summary>The result of one exchange.</summary>
    /// <param name="NoResponse">True when nothing final reached the client, so the caller should answer with <paramref name="ErrorStatus"/>.</param>
    /// <param name="ClosedEarly">True when the server closed or reset before any response byte, and no interim response was relayed.</param>
    /// <param name="ErrorStatus">The status to answer with when <paramref name="NoResponse"/> is true.</param>
    /// <param name="ErrorDetail">The relay-written explanation for that answer.</param>
    /// <param name="Status">The status to log.</param>
    /// <param name="CloseClient">True when the client connection must close after this exchange.</param>
    /// <param name="DropServer">True when the server connection mustn't be reused.</param>
    /// <param name="RequestBytes">Request body bytes sent on.</param>
    /// <param name="ResponseBytes">Response body bytes sent to the client.</param>
    private readonly record struct ExchangeOutcome(
        bool NoResponse, bool ClosedEarly, int ErrorStatus, string? ErrorDetail, int Status, bool CloseClient, bool DropServer, long RequestBytes, long ResponseBytes)
    {
        public static ExchangeOutcome Failed(int status, string detail, long requestBytes, bool closedEarly = false) =>
            new(true, closedEarly, status, detail, status, true, true, requestBytes, 0);

        /// <summary>True when the response broke off after its head reached the client.</summary>
        public bool BrokeOff { get; init; }
    }

    private async Task<ExchangeOutcome> ExchangeAsync(RelayRuntime runtime, RequestHead request, BodyFraming requestFraming, bool clientWantsClose,
        ServerConnection server, CancellationToken cancellationToken)
    {
        using var exchangeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // The wait for the response head restarts whenever request body bytes go out, so a long upload isn't cut off.
        using var headIdle = new IdleTimer(runtime.IdleTimeout, exchangeCts.Token);
        var requestBody = new ActivityStream(server.Stream, headIdle.Touch, _host.Traffic.AddUp);
        var bodyFailed = false;
        var bodyTask = requestFraming.Kind == BodyKind.None ? Task.CompletedTask : CopyRequestBodyAsync();

        async Task CopyRequestBodyAsync()
        {
            try
            {
                await _clientReader.CopyBodyAsync(requestFraming, requestBody, runtime.Limits.MaxHeaderBytes, exchangeCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or HttpFramingException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
                bodyFailed = true;
                await exchangeCts.CancelAsync().ConfigureAwait(false);
            }
        }

        var responseStarted = false;
        try
        {
            ResponseHead? response;
            HeadOutcome outcome;
            var interimRelayed = false;
            while (true)
            {
                headIdle.Touch();
                (response, outcome) = await ReadResponseHeadAsync(runtime, server, headIdle.Token, cancellationToken).ConfigureAwait(false);
                if (response is null || response.StatusCode is < 100 or > 199)
                {
                    break;
                }

                if (response.StatusCode == 101)
                {
                    // Upgrade is stripped from requests, so a 101 means the server ignored the protocol. Don't follow it.
                    return ExchangeOutcome.Failed(502, "The server tried to switch protocols.", requestBody.BytesWritten);
                }

                await _clientStream.WriteAsync(HeadWriter.BuildResponse(response, close: false), cancellationToken).ConfigureAwait(false);
                interimRelayed = true;
            }

            if (bodyFailed)
            {
                return ExchangeOutcome.Failed(400, "The request body was malformed or cut short.", requestBody.BytesWritten);
            }

            if (response is null)
            {
                return outcome switch
                {
                    HeadOutcome.TimedOut => ExchangeOutcome.Failed(504, "The server didn't answer in time.", requestBody.BytesWritten),
                    HeadOutcome.Closed => ExchangeOutcome.Failed(502, "The server closed the connection without answering.", requestBody.BytesWritten,
                        closedEarly: !interimRelayed && requestBody.BytesWritten == 0),
                    _ => ExchangeOutcome.Failed(502, "The server sent an invalid response.", requestBody.BytesWritten),
                };
            }

            if (!Framing.TryGetResponseFraming(request.Method, response, out var responseFraming, out _))
            {
                return ExchangeOutcome.Failed(502, "The server sent an invalid response.", requestBody.BytesWritten);
            }

            var bodyDone = bodyTask.IsCompletedSuccessfully && !bodyFailed;
            var serverWantsClose = Framing.WantsClose(response.MinorVersion, response.Headers) || responseFraming.Kind == BodyKind.CloseDelimited;
            var closeClient = clientWantsClose || serverWantsClose || !bodyDone;
            responseStarted = true;
            await _clientStream.WriteAsync(HeadWriter.BuildResponse(response, closeClient), cancellationToken).ConfigureAwait(false);

            var responseBytes = await CopyWithIdleAsync(server.Reader, responseFraming, _clientStream, runtime, cancellationToken).ConfigureAwait(false);
            if (!bodyDone)
            {
                // The server answered before reading the whole request body. Stop sending it; both connections close.
                await exchangeCts.CancelAsync().ConfigureAwait(false);
            }

            return new ExchangeOutcome(false, false, 0, null, response.StatusCode, closeClient, serverWantsClose || !bodyDone, requestBody.BytesWritten, responseBytes);
        }
        catch (Exception ex) when (ex is IOException or HttpFramingException or SocketException or ObjectDisposedException)
        {
            // Once a final head has gone to the client the only safe answer is to close everything; before that, send a 502.
            return responseStarted
                ? new ExchangeOutcome(false, false, 0, null, 502, true, true, requestBody.BytesWritten, 0) { BrokeOff = true }
                : ExchangeOutcome.Failed(502, "The connection to the server failed.", requestBody.BytesWritten);
        }
        finally
        {
            await exchangeCts.CancelAsync().ConfigureAwait(false);
            try
            {
                await bodyTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Cancelled because the exchange ended.
            }
        }
    }

    /// <summary>Reads one response head. Never throws for timeouts, resets or bad heads: the outcome says which it was.</summary>
    private static async Task<(ResponseHead? Head, HeadOutcome Outcome)> ReadResponseHeadAsync(
        RelayRuntime runtime, ServerConnection server, CancellationToken readToken, CancellationToken outer)
    {
        HeadReadResult read;
        try
        {
            read = await server.Reader.ReadHeadAsync(runtime.Limits.MaxHeaderBytes, readToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!outer.IsCancellationRequested)
        {
            return (null, HeadOutcome.TimedOut);
        }
        catch (Exception ex) when (ex is IOException or SocketException)
        {
            return (null, HeadOutcome.Closed);
        }

        if (read.Status == HeadReadStatus.EndOfStream)
        {
            return (null, HeadOutcome.Closed);
        }

        return read.Status == HeadReadStatus.Ok
            && HeadParser.TryParseResponse(read.Head.Span, runtime.Limits.MaxHeaderCount, out var response, out _)
            ? (response, HeadOutcome.Ok)
            : (null, HeadOutcome.Invalid);
    }

    /// <summary>Copies a body with an idle timeout that resets on every write. A stall surfaces as an <see cref="IOException"/>.</summary>
    private async Task<long> CopyWithIdleAsync(HttpReader from, BodyFraming framing, Stream to, RelayRuntime runtime, CancellationToken cancellationToken)
    {
        using var idle = new IdleTimer(runtime.IdleTimeout, cancellationToken);
        var destination = new ActivityStream(to, idle.Touch, _host.Traffic.AddDown);
        try
        {
            await from.CopyBodyAsync(framing, destination, runtime.Limits.MaxHeaderBytes, idle.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException("The body stalled for longer than the idle timeout.", ex);
        }

        return destination.BytesWritten;
    }

    /// <summary>
    /// Makes sure a live server connection for <paramref name="key"/> is open. A connection is reused only under the same
    /// settings it was opened with, so a changed upstream or deny list takes effect on the very next request.
    /// Returns true when an existing connection was reused.
    /// </summary>
    private async Task<bool> EnsureServerAsync(RelayRuntime runtime, string key, NormalisedHost host, int port, bool toUpstream, CancellationToken cancellationToken)
    {
        if (_server is not null && ReferenceEquals(_server.Runtime, runtime) && _server.Key == key && _server.IsReusable())
        {
            Log.ServerReused(Logger, host.Value, port);
            return true;
        }

        await DropServerAsync().ConfigureAwait(false);
        var socket = toUpstream
            ? await ConnectUpstreamAsync(runtime, runtime.HttpUpstream!, cancellationToken).ConfigureAwait(false)
            : await Connector.ConnectDirectAsync(host, port, runtime.Router, runtime.ConnectTimeout, cancellationToken).ConfigureAwait(false);
        _server = new ServerConnection(socket, runtime, key);
        Log.ServerOpened(Logger, host.Value, port, toUpstream ? RouteKind.Upstream : RouteKind.Direct);
        return false;
    }

    private async Task<Socket> ConnectUpstreamAsync(RelayRuntime runtime, UpstreamEndpoint upstream, CancellationToken cancellationToken)
    {
        try
        {
            var socket = await Connector.ConnectUpstreamAsync(upstream, runtime.Router.ListenPort, runtime.ConnectTimeout, cancellationToken)
                .ConfigureAwait(false);
            _host.OnUpstreamResult(upstream, true);
            return socket;
        }
        catch (RelayException)
        {
            _host.OnUpstreamResult(upstream, false);
            throw;
        }
    }

    private async Task DropServerAsync()
    {
        if (_server is not null)
        {
            await _server.DisposeAsync().ConfigureAwait(false);
            _server = null;
        }
    }

    private async Task RefuseAsync(int status, string reason, string? detail, CancellationToken cancellationToken)
    {
        Log.Refused(Logger, status, reason);
        _host.Traffic.Error();
        try
        {
            await _clientStream.WriteAsync(HeadWriter.BuildError(status, detail), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
            // The client has gone; nothing to tell it.
        }
    }

    /// <summary>How a logged request counts in the traffic totals.</summary>
    private enum CountAs
    {
        /// <summary>A response reached the client: counts on its route.</summary>
        Completed,

        /// <summary>The response broke off part way: counts as an error.</summary>
        Error,

        /// <summary><see cref="RefuseAsync"/> already counted it as an error.</summary>
        AlreadyCounted,
    }

    private void LogRequest(RequestHead request, RouteDecision decision, int status, long up, long down, long started, CountAs countAs = CountAs.Completed)
    {
        if (countAs == CountAs.Completed)
        {
            _host.Traffic.Completed(decision.Kind);
        }
        else if (countAs == CountAs.Error)
        {
            _host.Traffic.Error();
        }

        if (Logger.IsEnabled(LogLevel.Information))
        {
            var elapsed = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            Log.Request(Logger, request.Method, request.Host, request.Port, decision.Kind, decision.Reason, status, up, down, elapsed);
        }
    }

    /// <summary>One outbound connection: to a destination, or to the upstream proxy.</summary>
    private sealed class ServerConnection : IAsyncDisposable
    {
        public ServerConnection(Socket socket, RelayRuntime runtime, string key)
        {
            Socket = socket;
            Runtime = runtime;
            Key = key;
            Stream = new NetworkStream(socket, ownsSocket: true);
            Reader = new HttpReader(Stream, runtime.BufferSize);
        }

        public Socket Socket { get; }

        /// <summary>The settings this connection was opened under.</summary>
        public RelayRuntime Runtime { get; }

        public string Key { get; }

        public NetworkStream Stream { get; }

        public HttpReader Reader { get; }

        /// <summary>
        /// True when the connection looks idle and open: no unread bytes from the server (which would be a protocol error)
        /// and no pending end of stream.
        /// </summary>
        public bool IsReusable()
        {
            try
            {
                return Reader.Buffered.IsEmpty && !Socket.Poll(0, SelectMode.SelectRead) && Socket.Available == 0;
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                return false;
            }
        }

        public async ValueTask DisposeAsync()
        {
            Reader.Dispose();
            await Stream.DisposeAsync().ConfigureAwait(false);
        }
    }
}
