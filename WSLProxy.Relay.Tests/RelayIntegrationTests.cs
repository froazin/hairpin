using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WSLProxy.Relay.Configuration;
using WSLProxy.Relay.Http;
using WSLProxy.Relay.Proxy;
using WSLProxy.Relay.Status;

namespace WSLProxy.Relay.Tests;

/// <summary>End-to-end checks over real loopback sockets: keep-alive reuse, CONNECT tunnelling, the HTTP and HTTPS upstreams and the local deny list.</summary>
[TestClass]
public sealed class RelayIntegrationTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [TestMethod]
    public async Task Plain_http_requests_on_one_client_connection_reuse_one_origin_connection()
    {
        using var cts = new CancellationTokenSource(Timeout);
        using var origin = new TcpListener(IPAddress.Loopback, 0);
        origin.Start();
        var originPort = ((IPEndPoint)origin.LocalEndpoint).Port;
        var accepted = 0;
        var heads = new List<string>();
        var originTask = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                using var socket = await origin.AcceptSocketAsync(cts.Token);
                Interlocked.Increment(ref accepted);
                await using var stream = new NetworkStream(socket);
                using var reader = new HttpReader(stream, 16 * 1024);
                while (true)
                {
                    var read = await reader.ReadHeadAsync(8192, cts.Token);
                    if (read.Status != HeadReadStatus.Ok)
                    {
                        break;
                    }

                    lock (heads)
                    {
                        heads.Add(Encoding.Latin1.GetString(read.Head.Span));
                    }

                    await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\nok"u8.ToArray(), cts.Token);
                }
            }
        }, cts.Token);

        await using var relay = await StartRelayAsync(denyLocal: false, cts.Token);
        using var client = await ConnectAsync(relay.Port, cts.Token);
        await using var clientStream = new NetworkStream(client);
        using var clientReader = new HttpReader(clientStream, 16 * 1024);

        foreach (var path in new[] { "/one", "/two?x=1" })
        {
            var request = $"GET http://127.0.0.1:{originPort}{path} HTTP/1.1\r\nHost: 127.0.0.1\r\nProxy-Connection: keep-alive\r\n\r\n";
            await clientStream.WriteAsync(Encoding.ASCII.GetBytes(request), cts.Token);
            var head = await clientReader.ReadHeadAsync(8192, cts.Token);
            Assert.StartsWith("HTTP/1.1 200 OK\r\n", Encoding.Latin1.GetString(head.Head.Span));
            using var body = new MemoryStream();
            await clientReader.CopyBodyAsync(new BodyFraming(BodyKind.ContentLength, 2), body, 1024, cts.Token);
            Assert.AreEqual("ok", Encoding.ASCII.GetString(body.ToArray()));
        }

        Assert.AreEqual(1, Volatile.Read(ref accepted), "The relay should reuse its origin connection.");
        lock (heads)
        {
            Assert.HasCount(2, heads);
            Assert.StartsWith($"GET /one HTTP/1.1\r\nHost: 127.0.0.1:{originPort}\r\n", heads[0]);
            Assert.StartsWith($"GET /two?x=1 HTTP/1.1\r\nHost: 127.0.0.1:{originPort}\r\n", heads[1]);
            Assert.IsFalse(heads[0].Contains("Proxy-Connection", StringComparison.OrdinalIgnoreCase));
        }

        await cts.CancelAsync();
        await Task.WhenAny(originTask);
    }

    [TestMethod]
    public async Task Non_idempotent_requests_are_never_retried_after_a_kept_alive_connection_drops()
    {
        using var cts = new CancellationTokenSource(Timeout);
        using var origin = new TcpListener(IPAddress.Loopback, 0);
        origin.Start();
        var originPort = ((IPEndPoint)origin.LocalEndpoint).Port;
        var accepted = 0;
        var posts = 0;
        var originTask = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                using var socket = await origin.AcceptSocketAsync(cts.Token);
                Interlocked.Increment(ref accepted);
                await using var stream = new NetworkStream(socket);
                using var reader = new HttpReader(stream, 16 * 1024);
                while ((await reader.ReadHeadAsync(8192, cts.Token)) is { Status: HeadReadStatus.Ok } read)
                {
                    if (read.Head.Span.StartsWith("POST "u8))
                    {
                        // Drop the connection without answering, as a server closing an idle connection would.
                        Interlocked.Increment(ref posts);
                        break;
                    }

                    await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\nok"u8.ToArray(), cts.Token);
                }
            }
        }, cts.Token);

        await using var relay = await StartRelayAsync(denyLocal: false, cts.Token);
        using var client = await ConnectAsync(relay.Port, cts.Token);
        await using var clientStream = new NetworkStream(client);
        using var clientReader = new HttpReader(clientStream, 16 * 1024);

        await clientStream.WriteAsync(Encoding.ASCII.GetBytes($"GET http://127.0.0.1:{originPort}/ HTTP/1.1\r\n\r\n"), cts.Token);
        Assert.StartsWith("HTTP/1.1 200 OK\r\n", Encoding.Latin1.GetString((await clientReader.ReadHeadAsync(8192, cts.Token)).Head.Span));
        await clientReader.CopyBodyAsync(new BodyFraming(BodyKind.ContentLength, 2), Stream.Null, 1024, cts.Token);

        await clientStream.WriteAsync(Encoding.ASCII.GetBytes($"POST http://127.0.0.1:{originPort}/charge HTTP/1.1\r\nContent-Length: 0\r\n\r\n"), cts.Token);
        Assert.StartsWith("HTTP/1.1 502 Bad Gateway\r\n", Encoding.Latin1.GetString((await clientReader.ReadHeadAsync(8192, cts.Token)).Head.Span));

        Assert.AreEqual(1, Volatile.Read(ref posts), "The POST must reach the origin exactly once.");
        Assert.AreEqual(1, Volatile.Read(ref accepted), "The relay mustn't open a second connection to retry a POST.");
        await cts.CancelAsync();
        await Task.WhenAny(originTask);
    }

    [TestMethod]
    public async Task Connect_tunnels_bytes_both_ways_and_counts_them_while_open()
    {
        using var cts = new CancellationTokenSource(Timeout);
        using var echo = new TcpListener(IPAddress.Loopback, 0);
        echo.Start();
        var echoPort = ((IPEndPoint)echo.LocalEndpoint).Port;
        var echoTask = Task.Run(async () =>
        {
            using var socket = await echo.AcceptSocketAsync(cts.Token);
            var buffer = new byte[64];
            int read;
            while ((read = await socket.ReceiveAsync(buffer, SocketFlags.None, cts.Token)) > 0)
            {
                await socket.SendAsync(buffer.AsMemory(0, read), SocketFlags.None, cts.Token);
            }
        }, cts.Token);

        await using var relay = await StartRelayAsync(denyLocal: false, cts.Token);
        using var client = await ConnectAsync(relay.Port, cts.Token);
        await using var stream = new NetworkStream(client);
        using var reader = new HttpReader(stream, 16 * 1024);

        await stream.WriteAsync(Encoding.ASCII.GetBytes($"CONNECT 127.0.0.1:{echoPort} HTTP/1.1\r\nHost: 127.0.0.1:{echoPort}\r\n\r\n"), cts.Token);
        var head = await reader.ReadHeadAsync(8192, cts.Token);
        Assert.AreEqual("HTTP/1.1 200 Connection established\r\n\r\n", Encoding.Latin1.GetString(head.Head.Span));

        await stream.WriteAsync("ping"u8.ToArray(), cts.Token);
        var reply = new byte[4];
        await stream.ReadExactlyAsync(reply, cts.Token);
        Assert.AreEqual("ping", Encoding.ASCII.GetString(reply));

        // Counted while the tunnel is still open, not only when it closes. The relay adds each count just after its write
        // completes, so give its thread a moment.
        var live = relay.Service.Read();
        while ((live.BytesUp < 4 || live.BytesDown < 4) && !cts.IsCancellationRequested)
        {
            await Task.Delay(10, cts.Token);
            live = relay.Service.Read();
        }

        Assert.AreEqual(4, live.BytesUp);
        Assert.AreEqual(4, live.BytesDown);
        Assert.AreEqual(1, live.ActiveConnections);

        client.Shutdown(SocketShutdown.Send);
        await echoTask.WaitAsync(cts.Token);
    }

    [TestMethod]
    public async Task Plain_requests_go_to_the_http_proxy_and_tunnels_to_the_https_proxy()
    {
        using var cts = new CancellationTokenSource(Timeout);
        using var httpProxy = new TcpListener(IPAddress.Loopback, 0);
        using var httpsProxy = new TcpListener(IPAddress.Loopback, 0);
        httpProxy.Start();
        httpsProxy.Start();
        var httpHead = AnswerOnceAsync(httpProxy, "HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n", cts.Token);
        var httpsHead = AnswerOnceAsync(httpsProxy, "HTTP/1.1 200 Connection established\r\n\r\n", cts.Token);

        // The HTTPS proxy sets only its port, so it takes the HTTP proxy's host. No health probes: each fake proxy takes one connection.
        await using var relay = await StartRelayAsync(denyLocal: true, cts.Token, options =>
        {
            options.Upstream.HealthCheckIntervalSeconds = 0;
            options.Upstream.Http = new UpstreamProxyOptions { Host = "127.0.0.1", Port = ((IPEndPoint)httpProxy.LocalEndpoint).Port };
            options.Upstream.Https = new UpstreamProxyOptions { Port = ((IPEndPoint)httpsProxy.LocalEndpoint).Port };
        });

        foreach (var request in new[] { "GET http://example.test/a HTTP/1.1\r\nHost: example.test\r\n\r\n", "CONNECT example.test:443 HTTP/1.1\r\nHost: example.test:443\r\n\r\n" })
        {
            using var client = await ConnectAsync(relay.Port, cts.Token);
            await using var stream = new NetworkStream(client);
            using var reader = new HttpReader(stream, 16 * 1024);
            await stream.WriteAsync(Encoding.ASCII.GetBytes(request), cts.Token);
            Assert.StartsWith("HTTP/1.1 200 ", Encoding.Latin1.GetString((await reader.ReadHeadAsync(8192, cts.Token)).Head.Span));
        }

        Assert.StartsWith("GET http://example.test/a HTTP/1.1\r\n", await httpHead);
        Assert.StartsWith("CONNECT example.test:443 HTTP/1.1\r\n", await httpsHead);
    }

    [TestMethod]
    public async Task Local_destinations_are_refused_by_default_and_counted_as_errors()
    {
        using var cts = new CancellationTokenSource(Timeout);
        await using var relay = await StartRelayAsync(denyLocal: true, cts.Token);
        using var client = await ConnectAsync(relay.Port, cts.Token);
        await using var stream = new NetworkStream(client);
        using var reader = new HttpReader(stream, 16 * 1024);

        await stream.WriteAsync("CONNECT 127.0.0.1:445 HTTP/1.1\r\nHost: 127.0.0.1:445\r\n\r\n"u8.ToArray(), cts.Token);
        var head = await reader.ReadHeadAsync(8192, cts.Token);
        Assert.StartsWith("HTTP/1.1 403 Forbidden\r\n", Encoding.Latin1.GetString(head.Head.Span));
        Assert.AreEqual(1, relay.Service.Read().Errors);
    }

    private static async Task<Socket> ConnectAsync(int port, CancellationToken cancellationToken)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port), cancellationToken);
        return socket;
    }

    /// <summary>Plays a proxy that accepts one connection, answers its first request head and returns that head.</summary>
    private static async Task<string> AnswerOnceAsync(TcpListener listener, string answer, CancellationToken cancellationToken)
    {
        using var socket = await listener.AcceptSocketAsync(cancellationToken);
        await using var stream = new NetworkStream(socket);
        using var reader = new HttpReader(stream, 16 * 1024);
        var head = Encoding.Latin1.GetString((await reader.ReadHeadAsync(8192, cancellationToken)).Head.Span);
        await stream.WriteAsync(Encoding.ASCII.GetBytes(answer), cancellationToken);
        return head;
    }

    private static async Task<RunningRelay> StartRelayAsync(bool denyLocal, CancellationToken cancellationToken, Action<RelayOptions>? configure = null)
    {
        var options = new RelayOptions { ListenPort = 0, DenyLocalDestinations = denyLocal };
        configure?.Invoke(options);
        var service = new RelayService(new FixedOptionsMonitor(options), NullLogger<RelayService>.Instance);
        await service.StartAsync(cancellationToken);
        while (service.Current.State != RelayState.Running)
        {
            await Task.Delay(20, cancellationToken);
        }

        var endpoint = service.Current.ListenEndpoint;
        var port = int.Parse(endpoint[(endpoint.LastIndexOf(':') + 1)..], CultureInfo.InvariantCulture);
        return new RunningRelay(service, port);
    }

    private sealed class RunningRelay(RelayService service, int port) : IAsyncDisposable
    {
        public RelayService Service { get; } = service;

        public int Port { get; } = port;

        public async ValueTask DisposeAsync()
        {
            await Service.StopAsync(CancellationToken.None);
            Service.Dispose();
        }
    }

    private sealed class FixedOptionsMonitor(RelayOptions options) : IOptionsMonitor<RelayOptions>
    {
        public RelayOptions CurrentValue => options;

        public RelayOptions Get(string? name) => options;

        public IDisposable? OnChange(Action<RelayOptions, string?> listener) => null;
    }
}
