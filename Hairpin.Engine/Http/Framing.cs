using System.Globalization;

namespace Hairpin.Engine.Http;

/// <summary>Works out body framing (RFC 9112 section 6.3). Pure and thread-safe.</summary>
public static class Framing
{
    /// <summary>
    /// Works out a request body's framing. Both Transfer-Encoding and Content-Length present: <see cref="HeadError.Malformed"/>.
    /// Transfer-Encoding must be exactly <c>chunked</c> (case-insensitive, one field, one coding), otherwise <see cref="HeadError.NotImplemented"/>.
    /// Content-Length must be one decimal of at most 18 digits, or repeated identical values (also comma-separated), otherwise <see cref="HeadError.Malformed"/>.
    /// Neither present: <see cref="BodyKind.None"/>. A request body is never close-delimited.
    /// </summary>
    public static bool TryGetRequestFraming(RequestHead request, out BodyFraming framing, out HeadError error)
    {
        ArgumentNullException.ThrowIfNull(request);
        framing = default;
        var transferEncodings = request.Headers.Where(f => IsNamed(f, "Transfer-Encoding")).ToList();
        var hasContentLength = request.Headers.Any(f => IsNamed(f, "Content-Length"));
        if (transferEncodings.Count > 0 && hasContentLength)
        {
            error = HeadError.Malformed;
            return false;
        }

        if (transferEncodings.Count > 0)
        {
            if (transferEncodings.Count != 1 || !transferEncodings[0].Value.Equals("chunked", StringComparison.OrdinalIgnoreCase))
            {
                error = HeadError.NotImplemented;
                return false;
            }

            framing = new BodyFraming(BodyKind.Chunked, 0);
            error = default;
            return true;
        }

        if (!hasContentLength)
        {
            framing = new BodyFraming(BodyKind.None, 0);
            error = default;
            return true;
        }

        if (!TryGetContentLength(request.Headers, out var length))
        {
            error = HeadError.Malformed;
            return false;
        }

        framing = length == 0 ? new BodyFraming(BodyKind.None, 0) : new BodyFraming(BodyKind.ContentLength, length);
        error = default;
        return true;
    }

    /// <summary>
    /// Works out a response body's framing, given the request method: no body for HEAD, 1xx, 204 and 304; chunked when the final
    /// Transfer-Encoding coding is <c>chunked</c> (Content-Length is then ignored, and <see cref="HeadWriter"/> drops it);
    /// close-delimited when Transfer-Encoding is present but chunked isn't last; Content-Length when valid (invalid or conflicting
    /// values: <see cref="HeadError.Malformed"/>, so the relay sends 502); otherwise close-delimited.
    /// </summary>
    public static bool TryGetResponseFraming(string requestMethod, ResponseHead response, out BodyFraming framing, out HeadError error)
    {
        ArgumentNullException.ThrowIfNull(response);
        error = default;
        if (requestMethod == "HEAD" || response.StatusCode is < 200 or 204 or 304)
        {
            framing = new BodyFraming(BodyKind.None, 0);
            return true;
        }

        var hasTransferEncoding = response.Headers.Any(f => IsNamed(f, "Transfer-Encoding"));
        var codings = response.Headers.Where(f => IsNamed(f, "Transfer-Encoding")).SelectMany(f => SplitList(f.Value)).ToList();
        if (hasTransferEncoding && codings.Count == 0)
        {
            // A Transfer-Encoding field with no codings would be framed one way here and another way by the client.
            framing = default;
            error = HeadError.Malformed;
            return false;
        }

        if (codings.Count > 0)
        {
            framing = codings[^1].Equals("chunked", StringComparison.OrdinalIgnoreCase)
                ? new BodyFraming(BodyKind.Chunked, 0)
                : new BodyFraming(BodyKind.CloseDelimited, 0);
            return true;
        }

        if (response.Headers.Any(f => IsNamed(f, "Content-Length")))
        {
            if (!TryGetContentLength(response.Headers, out var length))
            {
                framing = default;
                error = HeadError.Malformed;
                return false;
            }

            framing = length == 0 ? new BodyFraming(BodyKind.None, 0) : new BodyFraming(BodyKind.ContentLength, length);
            return true;
        }

        framing = new BodyFraming(BodyKind.CloseDelimited, 0);
        return true;
    }

    /// <summary>
    /// True when the sender wants the connection closed after this message: a <c>close</c> token in Connection,
    /// or HTTP/1.0 without a <c>keep-alive</c> token.
    /// </summary>
    public static bool WantsClose(int minorVersion, IReadOnlyList<HeaderField> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        var close = false;
        var keepAlive = false;
        foreach (var token in ConnectionTokens(headers))
        {
            close |= token.Equals("close", StringComparison.OrdinalIgnoreCase);
            keepAlive |= token.Equals("keep-alive", StringComparison.OrdinalIgnoreCase);
        }

        return close || (minorVersion == 0 && !keepAlive);
    }

    /// <summary>Every token listed in the Connection fields, trimmed.</summary>
    internal static IEnumerable<string> ConnectionTokens(IReadOnlyList<HeaderField> headers) =>
        headers.Where(f => IsNamed(f, "Connection"))
            .SelectMany(f => SplitList(f.Value));

    /// <summary>Reads Content-Length: every value across every field must be the same 1 to 18 digit decimal.</summary>
    internal static bool TryGetContentLength(IReadOnlyList<HeaderField> headers, out long length)
    {
        length = -1;
        foreach (var field in headers.Where(f => IsNamed(f, "Content-Length")))
        {
            foreach (var part in SplitList(field.Value, keepEmpty: true))
            {
                if (part.Length is 0 or > 18 || !part.All(char.IsAsciiDigit))
                {
                    return false;
                }

                var value = long.Parse(part, NumberStyles.None, CultureInfo.InvariantCulture);
                if (length >= 0 && value != length)
                {
                    return false;
                }

                length = value;
            }
        }

        return length >= 0;
    }

    internal static bool IsNamed(HeaderField field, string name) => field.Name.Equals(name, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Splits a comma-separated field value, trimming only SP and HTAB (RFC 9110 OWS). Unicode trimming would drop obs-text
    /// bytes such as 0xA0, so the relay would read <c>chunked\xA0</c> as <c>chunked</c> while the next hop wouldn't.
    /// </summary>
    internal static IEnumerable<string> SplitList(string value, bool keepEmpty = false)
    {
        foreach (var part in value.Split(','))
        {
            var trimmed = part.Trim(' ', '\t');
            if (keepEmpty || trimmed.Length > 0)
            {
                yield return trimmed;
            }
        }
    }
}
