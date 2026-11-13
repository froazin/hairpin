using System.Globalization;
using System.Net;
using System.Net.Sockets;
using WSLProxy.Relay.Configuration;
using WSLProxy.Relay.Routing;

namespace WSLProxy.Relay.Proxy;

/// <summary>Opens outbound TCP connections. IPv4 only, using the Windows system resolver.</summary>
internal static class Connector
{
    /// <summary>
    /// Connects straight to a destination. Names are resolved once, every address the deny list rejects is dropped, and the
    /// socket connects only to the addresses that were checked, so a second lookup can't swap in a local address.
    /// </summary>
    public static async Task<Socket> ConnectDirectAsync(NormalisedHost host, int port, Router router, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        IPAddress[] candidates;
        if (host.Kind == HostKind.IPv4)
        {
            candidates = [host.Address!];
        }
        else
        {
            candidates = await ResolveAsync(host.Value, timeoutCts.Token, cancellationToken).ConfigureAwait(false);
            if (candidates.Length == 0)
            {
                throw new RelayException(502, "DNS lookup for the destination found no IPv4 address.");
            }
        }

        var allowed = candidates
            .Where(a => !DestinationPolicy.IsDeniedAddress(a, port, router.ListenPort, router.DenyLocal, out _))
            .ToArray();
        if (allowed.Length == 0)
        {
            throw new RelayException(403, "The destination resolves to a local or reserved address.");
        }

        return await ConnectAsync(allowed, port,
            "The destination refused or dropped the connection.", "The destination didn't accept the connection in time.",
            timeoutCts.Token, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Connects to the configured upstream proxy. The user chose it, so the local deny list doesn't apply, but an address that
    /// would loop back to the relay's own port is still refused.
    /// </summary>
    public static async Task<Socket> ConnectUpstreamAsync(UpstreamOptions upstream, int listenPort, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        var name = Describe(upstream);
        var hostName = upstream.Host.Trim();
        IPAddress[] addresses = HostNormaliser.TryParseStrictIPv4(hostName, out var literal)
            ? [literal]
            : await ResolveAsync(hostName, timeoutCts.Token, cancellationToken).ConfigureAwait(false);
        if (addresses.Length == 0)
        {
            throw new RelayException(502, $"DNS lookup for the upstream proxy {name} found no IPv4 address. Check the endpoint agent is connected.");
        }

        addresses = [.. addresses.Where(a => !DestinationPolicy.IsDeniedAddress(a, upstream.Port, listenPort, denyLocal: false, out _))];
        if (addresses.Length == 0)
        {
            throw new RelayException(502, $"The upstream proxy {name} points back at the relay itself.");
        }

        return await ConnectAsync(addresses, upstream.Port,
            $"The upstream proxy {name} refused the connection. Check the endpoint agent is connected.",
            $"The upstream proxy {name} didn't answer. Check the endpoint agent is connected.",
            timeoutCts.Token, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Formats the upstream proxy as <c>host:port</c> for messages and the UI.</summary>
    public static string Describe(UpstreamOptions upstream) =>
        upstream.Host.Trim() + ":" + upstream.Port.ToString(CultureInfo.InvariantCulture);

    private static async Task<IPAddress[]> ResolveAsync(string host, CancellationToken token, CancellationToken outer)
    {
        try
        {
            return await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, token).ConfigureAwait(false);
        }
        catch (SocketException ex)
        {
            throw new RelayException(502, "DNS lookup failed: " + ex.SocketErrorCode.ToString() + ".");
        }
        catch (OperationCanceledException) when (!outer.IsCancellationRequested)
        {
            throw new RelayException(504, "DNS lookup didn't finish in time.");
        }
    }

    private static async Task<Socket> ConnectAsync(IPAddress[] addresses, int port, string refused, string timedOut, CancellationToken token, CancellationToken outer)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(addresses, port, token).ConfigureAwait(false);
            return socket;
        }
        catch (SocketException)
        {
            socket.Dispose();
            throw new RelayException(502, refused);
        }
        catch (OperationCanceledException) when (!outer.IsCancellationRequested)
        {
            socket.Dispose();
            throw new RelayException(504, timedOut);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
