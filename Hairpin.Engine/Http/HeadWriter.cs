using System.Globalization;
using System.Text;

namespace Hairpin.Engine.Http;

/// <summary>Builds the heads the relay sends on. Pure and thread-safe. Output is Latin-1 bytes ending in CRLF CRLF.</summary>
/// <remarks>
/// Hop-by-hop fields are removed from every forwarded head: each field named in a Connection field, then Connection,
/// Proxy-Connection, Keep-Alive, TE, Trailer, Upgrade, Proxy-Authorization and Proxy-Authenticate. The parser has already
/// rejected heads whose Connection field names Content-Length, Transfer-Encoding or Host, so framing is never stripped.
/// Content-Length is re-emitted once, in canonical form, and is dropped when Transfer-Encoding is present (RFC 9112 section 6.3).
/// <c>Via: 1.1 hairpin</c> is appended after any existing Via.
/// </remarks>
public static class HeadWriter
{
    /// <summary>The name the relay gives itself in Via, as the received-by part of its entry.</summary>
    public const string ViaReceivedBy = "hairpin";

    /// <summary>The Via value the relay adds.</summary>
    public const string ViaValue = "1.1 " + ViaReceivedBy;

    private static readonly char[] Whitespace = [' ', '\t'];

    /// <summary>
    /// True when a Via field value has an entry this relay added: one whose received-by is exactly
    /// <see cref="ViaReceivedBy"/>, ignoring case. A proxy whose name only contains the word, such as <c>hairpin-gw</c>,
    /// doesn't count.
    /// </summary>
    public static bool HasOwnVia(string viaValue)
    {
        ArgumentNullException.ThrowIfNull(viaValue);
        foreach (var entry in viaValue.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            // An entry is received-protocol, received-by and an optional comment, separated by whitespace (RFC 9110
            // section 7.6.3).
            var parts = entry.Split(Whitespace, 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && string.Equals(parts[1], ViaReceivedBy, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static readonly HashSet<string> HopByHop = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Proxy-Connection", "Keep-Alive", "TE", "Trailer", "Upgrade", "Proxy-Authorization", "Proxy-Authenticate",
    };

    /// <summary>
    /// Builds a request head to send on. When <paramref name="toUpstreamProxy"/> is true the target stays in absolute form
    /// (<see cref="RequestHead.RawTarget"/>); when false it becomes origin form (<see cref="RequestHead.PathAndQuery"/>).
    /// Either way, any received Host field is dropped and a new <c>Host</c> is written first, from the target:
    /// <c>host</c>, or <c>host:port</c> when the port isn't 80. The version is always <c>HTTP/1.1</c>.
    /// Adds <c>Connection: close</c> when <paramref name="close"/> is true.
    /// </summary>
    public static byte[] BuildRequest(RequestHead request, bool toUpstreamProxy, bool close)
    {
        ArgumentNullException.ThrowIfNull(request);
        var sb = new StringBuilder(512);
        sb.Append(request.Method).Append(' ')
            .Append(toUpstreamProxy ? request.RawTarget : request.PathAndQuery)
            .Append(" HTTP/1.1\r\n");
        sb.Append("Host: ").Append(request.Host);
        if (request.Port != 80)
        {
            sb.Append(':').Append(request.Port.ToString(CultureInfo.InvariantCulture));
        }

        sb.Append("\r\n");
        AppendFields(sb, request.Headers, skipHost: true);
        AppendTail(sb, close);
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    /// <summary>
    /// Builds a response head to send to the client: <c>HTTP/1.1</c>, the received status and reason,
    /// hop-by-hop fields removed, Via added, and <c>Connection: close</c> when <paramref name="close"/> is true.
    /// </summary>
    public static byte[] BuildResponse(ResponseHead response, bool close)
    {
        ArgumentNullException.ThrowIfNull(response);
        var sb = new StringBuilder(512);
        sb.Append("HTTP/1.1 ").Append(response.StatusCode.ToString(CultureInfo.InvariantCulture)).Append(' ')
            .Append(response.ReasonPhrase).Append("\r\n");
        // 1xx and 204 responses never have a body, so framing fields on them would only mislead a lenient client.
        AppendFields(sb, response.Headers, skipHost: false, dropFraming: response.StatusCode is < 200 or 204);
        AppendTail(sb, close);
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    /// <summary>Builds the CONNECT request sent to an upstream proxy: request line, Host, Via, blank line.</summary>
    public static byte[] BuildConnect(string host, int port)
    {
        var authority = host + ":" + port.ToString(CultureInfo.InvariantCulture);
        return Encoding.Latin1.GetBytes($"CONNECT {authority} HTTP/1.1\r\nHost: {authority}\r\nVia: {ViaValue}\r\n\r\n");
    }

    /// <summary>
    /// Builds a complete error response generated by the relay itself: status line, <c>Content-Type: text/plain; charset=utf-8</c>,
    /// Content-Length, <c>Connection: close</c>, Via, and a short body: a fixed line for the status, then
    /// <paramref name="detail"/> on a second line when given. Never echoes request data.
    /// </summary>
    /// <param name="statusCode">The status code: 400, 403, 405, 414, 431, 501, 502, 503, 504 or 505.</param>
    /// <param name="detail">An optional explanation written by the relay. Never pass peer-supplied text.</param>
    public static byte[] BuildError(int statusCode, string? detail = null)
    {
        var (reason, message) = statusCode switch
        {
            400 => ("Bad Request", "The relay couldn't understand the request. It accepts only CONNECT and absolute-form http:// requests."),
            403 => ("Forbidden", "The relay doesn't forward to this destination."),
            405 => ("Method Not Allowed", "The relay doesn't forward this method."),
            414 => ("URI Too Long", "The request line is too long."),
            431 => ("Request Header Fields Too Large", "The request head is too large."),
            501 => ("Not Implemented", "The relay supports only chunked transfer coding."),
            502 => ("Bad Gateway", "The relay couldn't reach the destination or upstream proxy, or got an invalid response."),
            503 => ("Service Unavailable", "The relay has too many open connections."),
            504 => ("Gateway Timeout", "The destination or upstream proxy didn't answer in time."),
            505 => ("HTTP Version Not Supported", "The relay supports HTTP/1.1, and HTTP/1.0 only for CONNECT."),
            _ => throw new ArgumentOutOfRangeException(nameof(statusCode), statusCode, "Unsupported status code."),
        };

        var body = new StringBuilder()
            .Append(statusCode.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(reason).Append(": ").Append(message).Append('\n');
        if (!string.IsNullOrWhiteSpace(detail))
        {
            // The detail is relay-written, but strip control characters anyway so it can never break the framing.
            body.Append(new string([.. detail.Where(c => !char.IsControl(c))])).Append('\n');
        }

        var bodyBytes = Encoding.UTF8.GetBytes(body.ToString());
        var head = $"HTTP/1.1 {statusCode.ToString(CultureInfo.InvariantCulture)} {reason}\r\n"
            + "Content-Type: text/plain; charset=utf-8\r\n"
            + $"Content-Length: {bodyBytes.Length.ToString(CultureInfo.InvariantCulture)}\r\n"
            + "Connection: close\r\n"
            + $"Via: {ViaValue}\r\n\r\n";
        var headBytes = Encoding.Latin1.GetBytes(head);
        return [.. headBytes, .. bodyBytes];
    }

    /// <summary>Maps a parse error to the status code to send: request heads (<paramref name="isRequest"/>) or response heads (always 502).</summary>
    public static int StatusFor(HeadError error, bool isRequest) => !isRequest ? 502 : error switch
    {
        HeadError.RequestLineTooLong => 414,
        HeadError.HeadersTooLarge => 431,
        HeadError.MethodNotAllowed => 405,
        HeadError.VersionNotSupported => 505,
        HeadError.NotImplemented => 501,
        _ => 400,
    };

    private static void AppendFields(StringBuilder sb, IReadOnlyList<HeaderField> fields, bool skipHost, bool dropFraming = false)
    {
        var listed = new HashSet<string>(Framing.ConnectionTokens(fields), StringComparer.OrdinalIgnoreCase);
        var hasTransferEncoding = fields.Any(f => Framing.IsNamed(f, "Transfer-Encoding"));
        var contentLengthWritten = false;
        foreach (var field in fields)
        {
            if (HopByHop.Contains(field.Name) || listed.Contains(field.Name) || (skipHost && Framing.IsNamed(field, "Host"))
                || (dropFraming && (Framing.IsNamed(field, "Content-Length") || Framing.IsNamed(field, "Transfer-Encoding"))))
            {
                continue;
            }

            if (Framing.IsNamed(field, "Content-Length"))
            {
                // Re-emit once, canonically. The framing code has already rejected conflicting values.
                if (!hasTransferEncoding && !contentLengthWritten && Framing.TryGetContentLength(fields, out var length))
                {
                    sb.Append("Content-Length: ").Append(length.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
                    contentLengthWritten = true;
                }

                continue;
            }

            sb.Append(field.Name).Append(": ").Append(field.Value).Append("\r\n");
        }
    }

    private static void AppendTail(StringBuilder sb, bool close)
    {
        sb.Append("Via: ").Append(ViaValue).Append("\r\n");
        if (close)
        {
            sb.Append("Connection: close\r\n");
        }

        sb.Append("\r\n");
    }
}
