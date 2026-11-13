using System.Diagnostics;
using System.Net.Sockets;
using WSLProxy.Relay.Http;

namespace WSLProxy.Relay.Proxy;

/// <summary>Relays bytes both ways between two connections until both sides finish, one side fails or the tunnel goes idle.</summary>
internal static class Tunnel
{
    private static readonly TimeSpan HalfCloseGrace = TimeSpan.FromSeconds(30);

    /// <summary>One side of a tunnel: its socket, its stream and the reader that may already hold early bytes.</summary>
    public readonly record struct Side(Socket Socket, Stream Stream, HttpReader Reader);

    /// <summary>Runs the tunnel. Returns the bytes sent from <paramref name="a"/> to <paramref name="b"/> and back.</summary>
    public static async Task<(long AToB, long BToA)> RunAsync(Side a, Side b, TimeSpan idleTimeout, CancellationToken cancellationToken,
        Action<int>? onAToB = null, Action<int>? onBToA = null)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var lastActivity = Stopwatch.GetTimestamp();
        void Touch() => Interlocked.Exchange(ref lastActivity, Stopwatch.GetTimestamp());

        var toB = new ActivityStream(b.Stream, Touch, onAToB);
        var toA = new ActivityStream(a.Stream, Touch, onBToA);
        var pumpAToB = PumpAsync(a.Reader, toB, b.Socket, cts.Token);
        var pumpBToA = PumpAsync(b.Reader, toA, a.Socket, cts.Token);
        var watchdog = WatchdogAsync(() => Interlocked.Read(ref lastActivity), idleTimeout, cts);

        try
        {
            var first = await Task.WhenAny(pumpAToB, pumpBToA).ConfigureAwait(false);
            var other = first == pumpAToB ? pumpBToA : pumpAToB;
            if (first.IsFaulted || first.IsCanceled)
            {
                await cts.CancelAsync().ConfigureAwait(false);
            }
            else
            {
                // One side finished sending. Give the other direction time to drain, as RFC 9110 section 9.3.6 asks.
                await Task.WhenAny(other, Task.Delay(HalfCloseGrace, cts.Token)).ConfigureAwait(false);
            }
        }
        finally
        {
            await cts.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(Observe(pumpAToB), Observe(pumpBToA), Observe(watchdog)).ConfigureAwait(false);
        }

        return (toB.BytesWritten, toA.BytesWritten);
    }

    private static async Task PumpAsync(HttpReader from, Stream to, Socket toSocket, CancellationToken cancellationToken)
    {
        await from.CopyToEndAsync(to, cancellationToken).ConfigureAwait(false);
        try
        {
            toSocket.Shutdown(SocketShutdown.Send);
        }
        catch (SocketException)
        {
            // The other side is already gone.
        }
        catch (ObjectDisposedException)
        {
            // The other side is already gone.
        }
    }

    private static async Task WatchdogAsync(Func<long> lastActivity, TimeSpan idleTimeout, CancellationTokenSource cts)
    {
        var interval = TimeSpan.FromSeconds(Math.Clamp(idleTimeout.TotalSeconds / 4, 1, 15));
        while (!cts.IsCancellationRequested)
        {
            await Task.Delay(interval, cts.Token).ConfigureAwait(false);
            if (Stopwatch.GetElapsedTime(lastActivity()) > idleTimeout)
            {
                await cts.CancelAsync().ConfigureAwait(false);
                return;
            }
        }
    }

    private static async Task Observe(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected when the tunnel ends.
        }
        catch (IOException)
        {
            // Resets and aborts end tunnels; nothing more to do.
        }
        catch (SocketException)
        {
            // As above.
        }
        catch (ObjectDisposedException)
        {
            // As above.
        }
    }
}
