using System.Text;
using Hairpin.Engine.Http;

namespace Hairpin.Engine.Tests;

/// <summary>Request and response parsing, body framing, chunked copying and header rewriting: the code that stops smuggling.</summary>
[TestClass]
public sealed class HttpTests
{
    private const int MaxLine = 8192;
    private const int MaxCount = 100;

    [TestMethod]
    [DataRow("CONNECT example.com:443 HTTP/1.1\r\nHost: example.com:443\r\n\r\n", "example.com", 443, "")]
    [DataRow("CONNECT example.com:443 HTTP/1.0\r\n\r\n", "example.com", 443, "")]
    [DataRow("GET http://example.com/a?b HTTP/1.1\r\nHost: other\r\n\r\n", "example.com", 80, "/a?b")]
    [DataRow("GET HTTP://example.com:8080 HTTP/1.1\r\n\r\n", "example.com", 8080, "/")]
    [DataRow("GET http://example.com?q HTTP/1.1\r\n\r\n", "example.com", 80, "/?q")]
    public void Request_heads_are_accepted(string head, string host, int port, string pathAndQuery)
    {
        Assert.IsTrue(HeadParser.TryParseRequest(Bytes(head), MaxLine, MaxCount, out var request, out var error), error.ToString());
        Assert.AreEqual(host, request.Host);
        Assert.AreEqual(port, request.Port);
        Assert.AreEqual(pathAndQuery, request.PathAndQuery);
    }

    [TestMethod]
    [DataRow("GET / HTTP/1.1\r\nHost: 127.0.0.1:3128\r\n\r\n", HeadError.Malformed)]
    [DataRow("POST / HTTP/1.1\r\nContent-Type: text/plain\r\n\r\n", HeadError.Malformed)]
    [DataRow("OPTIONS * HTTP/1.1\r\n\r\n", HeadError.Malformed)]
    [DataRow("GET https://example.com/ HTTP/1.1\r\n\r\n", HeadError.Malformed)]
    [DataRow("GET http://user@example.com/ HTTP/1.1\r\n\r\n", HeadError.Malformed)]
    [DataRow("GET http://[::1]/ HTTP/1.1\r\n\r\n", HeadError.Malformed)]
    [DataRow("GET http://example.com/#f HTTP/1.1\r\n\r\n", HeadError.Malformed)]
    [DataRow("CONNECT example.com HTTP/1.1\r\n\r\n", HeadError.Malformed)]
    [DataRow("CONNECT example.com:0 HTTP/1.1\r\n\r\n", HeadError.Malformed)]
    [DataRow("CONNECT example.com:443 HTTP/1.1\r\nContent-Length: 5\r\n\r\n", HeadError.Malformed)]
    [DataRow("GET http://a/ HTTP/1.1\r\nHost: a\r\nHost: b\r\n\r\n", HeadError.Malformed)]
    [DataRow("GET http://a/ HTTP/1.1\r\nX: y\r\n z\r\n\r\n", HeadError.Malformed)]
    [DataRow("GET http://a/ HTTP/1.1\r\nX : y\r\n\r\n", HeadError.Malformed)]
    [DataRow("GET http://a/ HTTP/1.1\r\nX: y\n\r\n", HeadError.Malformed)]
    [DataRow("GET http://a/ HTTP/1.1\r\nX: a\u0000b\r\n\r\n", HeadError.Malformed)]
    [DataRow("POST http://a/ HTTP/1.1\r\nConnection: Content-Length\r\nContent-Length: 5\r\n\r\n", HeadError.Malformed)]
    [DataRow("POST http://a/ HTTP/1.1\r\nConnection: transfer-encoding\r\nTransfer-Encoding: chunked\r\n\r\n", HeadError.Malformed)]
    [DataRow("TRACE http://a/ HTTP/1.1\r\n\r\n", HeadError.MethodNotAllowed)]
    [DataRow("GET http://a/ HTTP/1.0\r\n\r\n", HeadError.VersionNotSupported)]
    [DataRow("GET http://a/ HTTP/2.0\r\n\r\n", HeadError.VersionNotSupported)]
    public void Request_heads_that_could_smuggle_or_target_the_relay_are_rejected(string head, HeadError expected)
    {
        Assert.IsFalse(HeadParser.TryParseRequest(Bytes(head), MaxLine, MaxCount, out _, out var error));
        Assert.AreEqual(expected, error);
    }

    [TestMethod]
    public void Request_limits_map_to_414_and_431()
    {
        var longLine = "GET http://a/" + new string('x', 100) + " HTTP/1.1\r\n\r\n";
        Assert.IsFalse(HeadParser.TryParseRequest(Bytes(longLine), 64, MaxCount, out _, out var lineError));
        Assert.AreEqual(414, HeadWriter.StatusFor(lineError, isRequest: true));

        var manyFields = "GET http://a/ HTTP/1.1\r\n" + string.Concat(Enumerable.Repeat("X: y\r\n", 3)) + "\r\n";
        Assert.IsFalse(HeadParser.TryParseRequest(Bytes(manyFields), MaxLine, 2, out _, out var countError));
        Assert.AreEqual(431, HeadWriter.StatusFor(countError, isRequest: true));
    }

    [TestMethod]
    [DataRow("HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\n", 200)]
    [DataRow("HTTP/1.1 204\r\n\r\n", 204)]
    [DataRow("HTTP/1.0 200 OK\nServer: old\n\n", 200)]
    [DataRow("HTTP/1.1 999 Nope\r\n\r\n", -1)]
    [DataRow("HTTP/1.1 200 OK\r\nConnection: content-length\r\nContent-Length: 2\r\n\r\n", -1)]
    [DataRow("HTTP/1.0 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n", -1)]
    [DataRow("HTTP/1.1 200 O\rK\r\n\r\n", -1)]
    public void Response_heads_allow_bare_lf_but_reject_desync_tricks(string head, int expectedStatus)
    {
        var ok = HeadParser.TryParseResponse(Bytes(head), MaxCount, out var response, out _);
        Assert.AreEqual(expectedStatus > 0, ok);
        if (ok)
        {
            Assert.AreEqual(expectedStatus, response!.StatusCode);
        }
    }

    [TestMethod]
    [DataRow("", BodyKind.None, 0L, true)]
    [DataRow("Content-Length: 5\r\n", BodyKind.ContentLength, 5L, true)]
    [DataRow("Content-Length: 5, 5\r\nContent-Length: 5\r\n", BodyKind.ContentLength, 5L, true)]
    [DataRow("Content-Length: 5\r\nContent-Length: 6\r\n", BodyKind.None, 0L, false)]
    [DataRow("Content-Length: +5\r\n", BodyKind.None, 0L, false)]
    [DataRow("Content-Length: 1234567890123456789\r\n", BodyKind.None, 0L, false)]
    [DataRow("Transfer-Encoding: chunked\r\nContent-Length: 5\r\n", BodyKind.None, 0L, false)]
    [DataRow("Transfer-Encoding: gzip, chunked\r\n", BodyKind.None, 0L, false)]
    [DataRow("Transfer-Encoding: Chunked\r\n", BodyKind.Chunked, 0L, true)]
    public void Request_framing_is_unambiguous(string fields, BodyKind kind, long length, bool ok)
    {
        Assert.IsTrue(HeadParser.TryParseRequest(Bytes("POST http://a/ HTTP/1.1\r\n" + fields + "\r\n"), MaxLine, MaxCount, out var request, out _));
        Assert.AreEqual(ok, Framing.TryGetRequestFraming(request, out var framing, out _));
        if (ok)
        {
            Assert.AreEqual(new BodyFraming(kind, length), framing);
        }
    }

    [TestMethod]
    [DataRow("HEAD", "200 OK", "Content-Length: 10\r\n", BodyKind.None)]
    [DataRow("GET", "204 No Content", "", BodyKind.None)]
    [DataRow("GET", "304 Not Modified", "Content-Length: 10\r\n", BodyKind.None)]
    [DataRow("GET", "100 Continue", "", BodyKind.None)]
    [DataRow("GET", "200 OK", "Transfer-Encoding: gzip, chunked\r\nContent-Length: 3\r\n", BodyKind.Chunked)]
    [DataRow("GET", "200 OK", "Transfer-Encoding: gzip\r\n", BodyKind.CloseDelimited)]
    [DataRow("GET", "200 OK", "Content-Length: 3\r\n", BodyKind.ContentLength)]
    [DataRow("GET", "200 OK", "", BodyKind.CloseDelimited)]
    public void Response_framing_follows_rfc_9112(string method, string status, string fields, BodyKind expected)
    {
        Assert.IsTrue(HeadParser.TryParseResponse(Bytes("HTTP/1.1 " + status + "\r\n" + fields + "\r\n"), MaxCount, out var response, out _));
        Assert.IsTrue(Framing.TryGetResponseFraming(method, response, out var framing, out _));
        Assert.AreEqual(expected, framing.Kind);
    }

    [TestMethod]
    [DataRow("Transfer-Encoding: ,\r\nContent-Length: 5\r\n", null)]
    [DataRow("Transfer-Encoding:  \r\nContent-Length: 5\r\n", null)]
    [DataRow("Transfer-Encoding: chunked\u00A0\r\n", BodyKind.CloseDelimited)]
    public void Response_framing_never_disagrees_with_the_client_about_transfer_coding(string fields, BodyKind? expected)
    {
        Assert.IsTrue(HeadParser.TryParseResponse(Bytes("HTTP/1.1 200 OK\r\n" + fields + "\r\n"), MaxCount, out var response, out _));
        var ok = Framing.TryGetResponseFraming("GET", response, out var framing, out _);
        Assert.AreEqual(expected is not null, ok);
        if (ok)
        {
            Assert.AreEqual(expected, framing.Kind);
        }
    }

    [TestMethod]
    public async Task Head_size_limit_holds_even_when_the_buffer_is_bigger()
    {
        var head = "GET http://a/ HTTP/1.1\r\n" + string.Concat(Enumerable.Repeat("X: " + new string('y', 400) + "\r\n", 100)) + "\r\n";
        using var source = new MemoryStream(Bytes(head));
        using var reader = new HttpReader(source, 64 * 1024);
        var result = await reader.ReadHeadAsync(32 * 1024, CancellationToken.None);
        Assert.AreEqual(HeadReadStatus.TooLarge, result.Status);
    }

    [TestMethod]
    [DataRow("5\r\nhello\r\n0\r\n\r\n", true)]
    [DataRow("5 ;name=\"v\"\r\nhello\r\na\r\n0123456789\r\n0\r\nX-Trailer: v\r\n\r\n", true)]
    [DataRow("5\nhello\r\n0\r\n\r\n", false)]
    [DataRow("5\r\nhelloXX0\r\n\r\n", false)]
    [DataRow("1000000000000000\r\n", false)]
    [DataRow("\r\n", false)]
    [DataRow("5;e\rx\r\nhello\r\n0\r\n\r\n", false)]
    [DataRow("5 \r\nhello\r\n0\r\n\r\n", false)]
    [DataRow("0\r\nnot a field\r\n\r\n", false)]
    [DataRow("0\r\n", false)]
    public async Task Chunked_bodies_are_copied_verbatim_and_never_past_their_end(string body, bool valid)
    {
        const string next = "GET http://next/ HTTP/1.1\r\n\r\n";
        using var source = new TrickleStream(Bytes(body + (valid ? next : string.Empty)));
        using var reader = new HttpReader(source, 16 * 1024);
        using var destination = new MemoryStream();

        Task Copy() => reader.CopyBodyAsync(new BodyFraming(BodyKind.Chunked, 0), destination, 1024, CancellationToken.None).AsTask();
        if (!valid)
        {
            await Assert.ThrowsAsync<Exception>(Copy);
            return;
        }

        await Copy();
        Assert.AreEqual(body, Encoding.Latin1.GetString(destination.ToArray()));

        // The pipelined request that followed the body must still be there, unconsumed.
        var nextHead = await reader.ReadHeadAsync(1024, CancellationToken.None);
        Assert.AreEqual(next, Encoding.Latin1.GetString(nextHead.Head.Span));
    }

    [TestMethod]
    [DataRow(false, "GET /p HTTP/1.1\r\nHost: example.com:8080\r\nContent-Length: 0\r\nAccept: */*\r\nVia: 1.1 proxy-a\r\nVia: 1.1 hairpin\r\n\r\n")]
    [DataRow(true, "GET http://example.com:8080/p HTTP/1.1\r\nHost: example.com:8080\r\nContent-Length: 0\r\nAccept: */*\r\nVia: 1.1 proxy-a\r\nVia: 1.1 hairpin\r\n\r\n")]
    public void Forwarded_requests_lose_hop_by_hop_fields_and_get_a_fresh_host(bool toUpstream, string expected)
    {
        const string head = "GET http://example.com:8080/p HTTP/1.1\r\nHost: evil.example\r\nConnection: keep-alive, X-Secret\r\nX-Secret: s\r\n"
            + "Proxy-Authorization: Basic eDp5\r\nProxy-Connection: keep-alive\r\nKeep-Alive: 5\r\nUpgrade: websocket\r\nContent-Length: 0, 0\r\n"
            + "Accept: */*\r\nVia: 1.1 proxy-a\r\n\r\n";
        Assert.IsTrue(HeadParser.TryParseRequest(Bytes(head), MaxLine, MaxCount, out var request, out _));
        Assert.AreEqual(expected, Encoding.Latin1.GetString(HeadWriter.BuildRequest(request, toUpstream, close: false)));
    }

    [TestMethod]
    public void Forwarded_responses_drop_content_length_when_transfer_encoding_is_present()
    {
        Assert.IsTrue(HeadParser.TryParseResponse(
            Bytes("HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\nContent-Length: 99\r\nKeep-Alive: timeout=5\r\n\r\n"), MaxCount, out var response, out _));
        Assert.AreEqual(
            "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\nVia: 1.1 hairpin\r\nConnection: close\r\n\r\n",
            Encoding.Latin1.GetString(HeadWriter.BuildResponse(response, close: true)));
    }

    [TestMethod]
    [DataRow("1.1 hairpin", true)]
    [DataRow("1.0 proxy-a, 1.1 HAIRPIN", true)]
    [DataRow("1.1\thairpin (comment)", true)]
    [DataRow("1.1 hairpin-gw", false)]
    [DataRow("1.1 proxy-a (behind hairpin)", false)]
    [DataRow("hairpin", false)]
    [DataRow("", false)]
    public void Only_the_relays_own_via_entry_counts_as_a_loop(string via, bool own)
    {
        Assert.AreEqual(own, HeadWriter.HasOwnVia(via));
    }

    private static byte[] Bytes(string text) => Encoding.Latin1.GetBytes(text);

    /// <summary>Returns at most three bytes per read, so parsers see every possible split point.</summary>
    private sealed class TrickleStream(byte[] data) : MemoryStream(data)
    {
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, 3));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, 3)], cancellationToken);
    }
}
