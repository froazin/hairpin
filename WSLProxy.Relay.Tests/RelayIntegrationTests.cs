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

/// <summary>End-to-end checks over real loopback sockets: keep-alive reuse, CONNECT tunnelling and the local deny list.</summary>
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
            StringAssert.StartsWith(Encoding.Latin1.GetString(head.Head.Span), "HTTP/1.1 200 OK\r\n");
            using var body = new MemoryStream();
            await clientReader.CopyBodyAsync(new BodyFraming(BodyKind.ContentLength, 2), body, 1024, cts.Token);
            Assert.AreEqual("ok", Encoding.ASCII.GetString(body.ToArray()));
        }

        Assert.AreEqual(1, Volatile.Read(ref accepted), "The relay should reuse its origin connection.");
        lock (heads)
        {
            Assert.HasCount(2, heads);
            StringAssert.StartsWith(heads[0], $"GET /one HTTP/1.1\r\nHost: 127.0.0.1:{originPort}\r\n");
            StringAssert.StartsWith(heads[1], $"GET /two?x=1 HTTP/1.1\r\nHost: 127.0.0.1:{originPort}\r\n");
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
        StringAssert.StartsWith(Encoding.Latin1.GetString((await clientReader.ReadHeadAsync(8192, cts.Token)).Head.Span), "HTTP/1.1 200 OK\r\n");
        await clientReader.CopyBodyAsync(new BodyFraming(BodyKind.ContentLength, 2), Stream.Null, 1024, cts.Token);

        await clientStream.WriteAsync(Encoding.ASCII.GetBytes($"POST http://127.0.0.1:{originPort}/charge HTTP/1.1\r\nContent-Length: 0\r\n\r\n"), cts.Token);
        StringAssert.StartsWith(Encoding.Latin1.GetString((await clientReader.ReadHeadAsync(8192, cts.Token)).Head.Span), "HTTP/1.1 502 Bad Gateway\r\n");

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

        // Counted while the tunnel is still open, not only when it closes.
        var live = relay.Service.Read();
        Assert.AreEqual(4, live.BytesUp);
        Assert.AreEqual(4, live.BytesDown);
        Assert.AreEqual(1, live.ActiveConnections);

        client.Shutdown(SocketShutdown.Send);
        await echoTask.WaitAsync(cts.Token);
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
        StringAssert.StartsWith(Encoding.Latin1.GetString(head.Head.Span), "HTTP/1.1 403 Forbidden\r\n");
        Assert.AreEqual(1, relay.Service.Read().Errors);
    }

    private static async Task<Socket> ConnectAsync(int port, CancellationToken cancellationToken)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port), cancellationToken);
        return socket;
    }

    private static async Task<RunningRelay> StartRelayAsync(bool denyLocal, CancellationToken cancellationToken)
    {
        var options = new RelayOptions { ListenPort = 0, DenyLocalDestinations = denyLocal };
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
