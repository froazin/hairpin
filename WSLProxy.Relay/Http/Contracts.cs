
namespace WSLProxy.Relay.Http;

/// <summary>Why a head was rejected, which decides the status code sent back.</summary>
public enum HeadError
{
    /// <summary>Malformed: 400 for a request, 502 for a response.</summary>
    Malformed,

    /// <summary>The request line is over the limit: 414.</summary>
    RequestLineTooLong,

    /// <summary>The head is over the size or field-count limit: 431 for a request, 502 for a response.</summary>
    HeadersTooLarge,

    /// <summary>The method isn't allowed, such as TRACE: 405.</summary>
    MethodNotAllowed,

    /// <summary>The HTTP version isn't supported, such as HTTP/1.0 for a non-CONNECT request: 505.</summary>
    VersionNotSupported,

    /// <summary>A Transfer-Encoding other than <c>chunked</c>: 501.</summary>
    NotImplemented,
}

/// <summary>One header field, name and value as received, with the value's surrounding whitespace trimmed.</summary>
/// <param name="Name">The field name, case preserved.</param>
/// <param name="Value">The field value.</param>
public readonly record struct HeaderField(string Name, string Value);

/// <summary>The form of a request's target.</summary>
public enum TargetForm
{
    /// <summary><c>CONNECT host:port</c>.</summary>
    Authority,

    /// <summary><c>GET http://host[:port]/path?query</c>.</summary>
    Absolute,
}

/// <summary>A parsed, validated request head. Immutable.</summary>
/// <param name="Method">The method token, case preserved (methods are case-sensitive).</param>
/// <param name="Form">The request-target form.</param>
/// <param name="Host">The target host, exactly as it appeared in the request target (not yet normalised).</param>
/// <param name="Port">The target port: explicit, or 80 for an absolute-form <c>http</c> target.</param>
/// <param name="PathAndQuery">For absolute form, the origin-form target to send direct, starting with <c>/</c> (an empty path becomes <c>/</c>). Empty for CONNECT.</param>
/// <param name="RawTarget">The request target exactly as received, for forwarding to an upstream proxy.</param>
/// <param name="MinorVersion">0 or 1, from <c>HTTP/1.x</c>.</param>
/// <param name="Headers">The header fields in order. Never null.</param>
public sealed record RequestHead(
    string Method,
    TargetForm Form,
    string Host,
    int Port,
    string PathAndQuery,
    string RawTarget,
    int MinorVersion,
    IReadOnlyList<HeaderField> Headers);

/// <summary>A parsed response head. Immutable.</summary>
/// <param name="MinorVersion">0 or 1, from <c>HTTP/1.x</c>.</param>
/// <param name="StatusCode">The three-digit status code, 100 to 599.</param>
/// <param name="ReasonPhrase">The reason phrase, possibly empty.</param>
/// <param name="Headers">The header fields in order. Never null.</param>
public sealed record ResponseHead(int MinorVersion, int StatusCode, string ReasonPhrase, IReadOnlyList<HeaderField> Headers);

/// <summary>How a message body is delimited (RFC 9112 section 6).</summary>
public enum BodyKind
{
    /// <summary>No body.</summary>
    None,

    /// <summary>Exactly <see cref="BodyFraming.Length"/> bytes.</summary>
    ContentLength,

    /// <summary>Chunked transfer coding, ending with a zero-length chunk and trailers.</summary>
    Chunked,

    /// <summary>Everything until the sender closes the connection. Responses only.</summary>
    CloseDelimited,
}

/// <summary>A body's framing.</summary>
/// <param name="Kind">How the body is delimited.</param>
/// <param name="Length">The byte count for <see cref="BodyKind.ContentLength"/>, otherwise 0.</param>
public readonly record struct BodyFraming(BodyKind Kind, long Length);

