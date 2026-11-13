using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace WSLProxy.Relay.Http;

/// <summary>
/// Parses request and response heads. Pure and thread-safe.
/// The input is the whole head: every byte up to and including the blank line.
/// </summary>
/// <remarks>
/// Request rules (RFC 9110 and 9112), each failure returning false with the matching <see cref="HeadError"/>:
/// <list type="bullet">
/// <item>Lines end in CRLF. A bare CR or bare LF, a NUL, or any other control character except HTAB inside a field value: <see cref="HeadError.Malformed"/>.</item>
/// <item>Request line: <c>method SP request-target SP HTTP/1.x</c>, single spaces. The method is an RFC 9110 token. The target is visible ASCII only.</item>
/// <item>CONNECT needs authority form <c>host:port</c>. Any other method needs absolute form with the <c>http</c> scheme. Origin form,
/// asterisk form, other schemes, userinfo, fragments and IPv6 literals are <see cref="HeadError.Malformed"/>, which stops browser pages
/// talking to the relay as if it were a web server.</item>
/// <item>TRACE: <see cref="HeadError.MethodNotAllowed"/>. HTTP/1.0 is accepted only for CONNECT.</item>
/// <item>Fields: <c>name ":" OWS value OWS</c>, with no whitespace before the colon and no obsolete line folding.</item>
/// <item>More than one Host, a Connection field that names Content-Length, Transfer-Encoding or Host (which would let a hop strip
/// the framing after the relay relied on it), or a CONNECT with a body: <see cref="HeadError.Malformed"/>.</item>
/// </list>
/// Responses follow the same field rules, except that bare LF line endings are accepted (the relay re-serialises heads with CRLF).
/// A response that names framing fields in Connection, or an HTTP/1.0 response with Transfer-Encoding, is <see cref="HeadError.Malformed"/>.
/// </remarks>
public static class HeadParser
{
    private static readonly Encoding Latin1 = Encoding.Latin1;

    /// <summary>Parses a request head.</summary>
    public static bool TryParseRequest(ReadOnlySpan<byte> head, int maxRequestLineBytes, int maxHeaderCount,
        [NotNullWhen(true)] out RequestHead? request, out HeadError error)
    {
        request = null;
        var lineEnd = head.IndexOf("\r\n"u8);
        if (lineEnd < 0)
        {
            error = head.Length > maxRequestLineBytes ? HeadError.RequestLineTooLong : HeadError.Malformed;
            return false;
        }

        if (lineEnd > maxRequestLineBytes)
        {
            error = HeadError.RequestLineTooLong;
            return false;
        }

        var line = head[..lineEnd];
        var firstSpace = line.IndexOf((byte)' ');
        var lastSpace = line.LastIndexOf((byte)' ');
        if (firstSpace <= 0 || lastSpace == firstSpace)
        {
            error = HeadError.Malformed;
            return false;
        }

        var methodBytes = line[..firstSpace];
        var targetBytes = line[(firstSpace + 1)..lastSpace];
        var versionBytes = line[(lastSpace + 1)..];
        if (!IsToken(methodBytes) || targetBytes.Length == 0 || !IsVisibleAscii(targetBytes))
        {
            error = HeadError.Malformed;
            return false;
        }

        if (!TryParseVersion(versionBytes, out var minor, out error))
        {
            return false;
        }

        var method = Latin1.GetString(methodBytes);
        var isConnect = method == "CONNECT";
        if (method == "TRACE")
        {
            error = HeadError.MethodNotAllowed;
            return false;
        }

        if (minor == 0 && !isConnect)
        {
            error = HeadError.VersionNotSupported;
            return false;
        }

        var target = Latin1.GetString(targetBytes);
        string host;
        int port;
        var pathAndQuery = string.Empty;
        if (isConnect)
        {
            if (!TryParseAuthority(target, requirePort: true, out host, out port, out var rest) || rest.Length != 0)
            {
                error = HeadError.Malformed;
                return false;
            }
        }
        else
        {
            if (!target.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || target.Contains('#', StringComparison.Ordinal)
                || !TryParseAuthority(target[7..], requirePort: false, out host, out port, out var rest))
            {
                error = HeadError.Malformed;
                return false;
            }

            pathAndQuery = rest.Length == 0 ? "/" : rest[0] == '?' ? "/" + rest : rest;
        }

        if (!TryParseFields(head[(lineEnd + 2)..], allowBareLf: false, maxHeaderCount, out var headers, out error))
        {
            return false;
        }

        var hostCount = 0;
        foreach (var field in headers)
        {
            if (field.Name.Equals("Host", StringComparison.OrdinalIgnoreCase))
            {
                hostCount++;
            }
            else if (isConnect && (field.Name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                || field.Name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)))
            {
                error = HeadError.Malformed;
                return false;
            }
        }

        if (hostCount > 1 || ConnectionNamesFramingField(headers))
        {
            error = HeadError.Malformed;
            return false;
        }

        request = new RequestHead(method, isConnect ? TargetForm.Authority : TargetForm.Absolute, host, port, pathAndQuery, target, minor, headers);
        error = default;
        return true;
    }

    /// <summary>Parses a response head.</summary>
    public static bool TryParseResponse(ReadOnlySpan<byte> head, int maxHeaderCount,
        [NotNullWhen(true)] out ResponseHead? response, out HeadError error)
    {
        response = null;
        var lf = head.IndexOf((byte)'\n');
        if (lf < 0)
        {
            error = HeadError.Malformed;
            return false;
        }

        var line = lf > 0 && head[lf - 1] == '\r' ? head[..(lf - 1)] : head[..lf];

        // "HTTP/1.x SSS" then an optional " reason".
        if (line.Length < 12 || line[8] != ' ' || !TryParseVersion(line[..8], out var minor, out _)
            || !char.IsAsciiDigit((char)line[9]) || !char.IsAsciiDigit((char)line[10]) || !char.IsAsciiDigit((char)line[11]))
        {
            error = HeadError.Malformed;
            return false;
        }

        var status = ((line[9] - '0') * 100) + ((line[10] - '0') * 10) + (line[11] - '0');
        if (status is < 100 or > 599 || (line.Length > 12 && line[12] != ' '))
        {
            error = HeadError.Malformed;
            return false;
        }

        var reasonBytes = line.Length > 13 ? line[13..] : [];
        if (!IsFieldValue(reasonBytes))
        {
            error = HeadError.Malformed;
            return false;
        }

        if (!TryParseFields(head[(lf + 1)..], allowBareLf: true, maxHeaderCount, out var headers, out error))
        {
            return false;
        }

        if (ConnectionNamesFramingField(headers)
            || (minor == 0 && headers.Any(f => f.Name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase))))
        {
            error = HeadError.Malformed;
            return false;
        }

        response = new ResponseHead(minor, status, Latin1.GetString(reasonBytes), headers);
        error = default;
        return true;
    }

    /// <summary>Parses the field lines after the start line, up to and including the blank line that ends the head.</summary>
    private static bool TryParseFields(ReadOnlySpan<byte> rest, bool allowBareLf, int maxHeaderCount, out List<HeaderField> fields, out HeadError error)
    {
        fields = [];
        while (true)
        {
            var lf = rest.IndexOf((byte)'\n');
            if (lf < 0)
            {
                error = HeadError.Malformed;
                return false;
            }

            ReadOnlySpan<byte> line;
            if (lf > 0 && rest[lf - 1] == '\r')
            {
                line = rest[..(lf - 1)];
            }
            else if (allowBareLf)
            {
                line = rest[..lf];
            }
            else
            {
                error = HeadError.Malformed;
                return false;
            }

            rest = rest[(lf + 1)..];
            if (line.Length == 0)
            {
                // The blank line ends the head; nothing may follow it in the head buffer.
                error = rest.Length == 0 ? default : HeadError.Malformed;
                return rest.Length == 0;
            }

            if (line[0] is (byte)' ' or (byte)'\t')
            {
                error = HeadError.Malformed; // Obsolete line folding.
                return false;
            }

            var colon = line.IndexOf((byte)':');
            if (colon <= 0 || !IsToken(line[..colon]))
            {
                error = HeadError.Malformed; // Includes whitespace before the colon.
                return false;
            }

            var value = TrimOws(line[(colon + 1)..]);
            if (!IsFieldValue(value))
            {
                error = HeadError.Malformed;
                return false;
            }

            if (fields.Count >= maxHeaderCount)
            {
                error = HeadError.HeadersTooLarge;
                return false;
            }

            fields.Add(new HeaderField(Latin1.GetString(line[..colon]), Latin1.GetString(value)));
        }
    }

    private static bool TryParseVersion(ReadOnlySpan<byte> version, out int minor, out HeadError error)
    {
        minor = 0;
        if (version.Length != 8 || !version.StartsWith("HTTP/"u8) || version[6] != '.'
            || !char.IsAsciiDigit((char)version[5]) || !char.IsAsciiDigit((char)version[7]))
        {
            error = HeadError.Malformed;
            return false;
        }

        if (version[5] != '1' || version[7] is not ((byte)'0' or (byte)'1'))
        {
            error = HeadError.VersionNotSupported;
            return false;
        }

        minor = version[7] - '0';
        error = default;
        return true;
    }

    /// <summary>
    /// Parses <c>host[:port]</c> from the start of <paramref name="text"/>. The host is letters, digits, hyphens, dots and
    /// underscores only. <paramref name="rest"/> is what follows the authority, which must be empty or start with <c>/</c> or <c>?</c>.
    /// </summary>
    private static bool TryParseAuthority(string text, bool requirePort, out string host, out int port, out string rest)
    {
        host = string.Empty;
        port = 80;
        rest = string.Empty;
        var i = 0;
        while (i < text.Length && (char.IsAsciiLetterOrDigit(text[i]) || text[i] is '-' or '.' or '_'))
        {
            i++;
        }

        if (i == 0)
        {
            return false;
        }

        host = text[..i];
        if (i < text.Length && text[i] == ':')
        {
            var start = ++i;
            while (i < text.Length && char.IsAsciiDigit(text[i]))
            {
                i++;
            }

            var digits = i - start;
            if (digits is 0 or > 5)
            {
                return false;
            }

            port = int.Parse(text.AsSpan(start, digits), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture);
            if (port is < 1 or > 65535)
            {
                return false;
            }
        }
        else if (requirePort)
        {
            return false;
        }

        rest = text[i..];
        return rest.Length == 0 || rest[0] is '/' or '?';
    }

    private static bool ConnectionNamesFramingField(List<HeaderField> headers)
    {
        foreach (var field in headers)
        {
            if (!field.Name.Equals("Connection", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var token in field.Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (token.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                    || token.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
                    || token.Equals("Host", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>True for an RFC 9110 token: one or more tchar.</summary>
    internal static bool IsToken(ReadOnlySpan<byte> value)
    {
        if (value.IsEmpty)
        {
            return false;
        }

        foreach (var b in value)
        {
            if (!IsTchar(b))
            {
                return false;
            }
        }

        return true;
    }

    internal static bool IsTchar(byte b) =>
        b is (>= (byte)'a' and <= (byte)'z') or (>= (byte)'A' and <= (byte)'Z') or (>= (byte)'0' and <= (byte)'9')
            or (byte)'!' or (byte)'#' or (byte)'$' or (byte)'%' or (byte)'&' or (byte)'\'' or (byte)'*' or (byte)'+'
            or (byte)'-' or (byte)'.' or (byte)'^' or (byte)'_' or (byte)'`' or (byte)'|' or (byte)'~';

    /// <summary>True when every byte is HTAB, SP, a visible ASCII character or obs-text (0x80 to 0xFF).</summary>
    internal static bool IsFieldValue(ReadOnlySpan<byte> value)
    {
        foreach (var b in value)
        {
            if (!(b == '\t' || b == ' ' || (b >= 0x21 && b != 0x7F)))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsVisibleAscii(ReadOnlySpan<byte> value)
    {
        foreach (var b in value)
        {
            if (b is < 0x21 or > 0x7E)
            {
                return false;
            }
        }

        return true;
    }

    private static ReadOnlySpan<byte> TrimOws(ReadOnlySpan<byte> value)
    {
        var start = 0;
        var end = value.Length;
        while (start < end && value[start] is (byte)' ' or (byte)'\t')
        {
            start++;
        }

        while (end > start && value[end - 1] is (byte)' ' or (byte)'\t')
        {
            end--;
        }

        return value[start..end];
    }
}
