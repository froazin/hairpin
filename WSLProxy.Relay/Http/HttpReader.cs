using System.Buffers;

namespace WSLProxy.Relay.Http;

/// <summary>The outcome of reading a head.</summary>
public enum HeadReadStatus
{
    /// <summary>A complete head was read. See <see cref="HeadReadResult.Head"/>.</summary>
    Ok,

    /// <summary>The peer closed the connection cleanly before sending any byte of a new head.</summary>
    EndOfStream,

    /// <summary>The peer closed the connection part way through a head.</summary>
    Truncated,

    /// <summary>The head grew past the size limit before its blank line arrived.</summary>
    TooLarge,
}

/// <summary>The result of <see cref="HttpReader.ReadHeadAsync"/>.</summary>
/// <param name="Status">What happened.</param>
/// <param name="Head">The whole head, up to and including the blank line, when <see cref="Status"/> is <see cref="HeadReadStatus.Ok"/>.</param>
public readonly record struct HeadReadResult(HeadReadStatus Status, ReadOnlyMemory<byte> Head);

/// <summary>Thrown when a chunked body breaks the chunked framing rules.</summary>
public sealed class HttpFramingException : Exception
{
    /// <summary>Creates the exception.</summary>
    public HttpFramingException() { }

    /// <summary>Creates the exception with a short message. Never include peer data in it.</summary>
    public HttpFramingException(string message) : base(message) { }

    /// <summary>Creates the exception with a message and inner exception.</summary>
    public HttpFramingException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// A buffered reader over one side of a connection. Reads heads with a size limit and copies bodies with
/// exact framing, so bytes of a following pipelined message are never consumed by mistake. Not thread-safe:
/// one reader per stream, used by one task at a time.
/// </summary>
/// <remarks>
/// Chunked bodies are relayed byte for byte, but every chunk-size line is parsed (1 to 15 hex digits, so the size fits in a
/// long; optional extensions of visible ASCII, space and tab; the whole line at most 4096 bytes; CRLF only), the CRLF after
/// each chunk's data is checked, and each trailer line must be a valid field line ending in CRLF, with the trailer section
/// limited in size. Any violation throws <see cref="HttpFramingException"/>; a premature end of stream throws
/// <see cref="EndOfStreamException"/>.
/// </remarks>
public sealed class HttpReader : IDisposable
{
    private const int MaxChunkLineBytes = 4096;

    private readonly Stream _stream;
    private byte[] _buffer;
    private int _start;
    private int _end;

    /// <summary>Creates a reader over <paramref name="stream"/>. The reader doesn't own or dispose the stream.</summary>
    /// <param name="stream">The stream to read.</param>
    /// <param name="bufferSize">The buffer size. Must be at least the largest head size the caller will ask for.</param>
    public HttpReader(Stream stream, int bufferSize)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfLessThan(bufferSize, MaxChunkLineBytes + 2);
        _stream = stream;
        _buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
    }

    /// <summary>The bytes read from the stream but not consumed yet, such as early tunnel data after a CONNECT head.</summary>
    public ReadOnlyMemory<byte> Buffered => _buffer.AsMemory(_start, _end - _start);

    /// <summary>
    /// Reads one head. The returned memory stays valid only until the next call on this reader.
    /// Cancel <paramref name="cancellationToken"/> to apply an idle or header timeout.
    /// </summary>
    /// <remarks>
    /// The head ends at the first blank line, whether it's CRLF or a bare LF. Accepting a bare LF here makes a malformed head
    /// fail fast in <see cref="HeadParser"/>, which decides whether bare LF line endings are allowed.
    /// </remarks>
    public async ValueTask<HeadReadResult> ReadHeadAsync(int maxHeadBytes, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_buffer.Length == 0, this);
        maxHeadBytes = Math.Min(maxHeadBytes, _buffer.Length);
        var scanned = 0;
        while (true)
        {
            var end = FindHeadEnd(_buffer.AsSpan(_start, _end - _start), scanned);
            if (end > maxHeadBytes)
            {
                // The pool may hand back a bigger buffer than asked for, so check the limit itself, not the buffer size.
                return new HeadReadResult(HeadReadStatus.TooLarge, default);
            }

            if (end > 0)
            {
                var head = _buffer.AsMemory(_start, end);
                _start += end;
                return new HeadReadResult(HeadReadStatus.Ok, head);
            }

            var buffered = _end - _start;
            if (buffered >= maxHeadBytes)
            {
                return new HeadReadResult(HeadReadStatus.TooLarge, default);
            }

            scanned = Math.Max(0, buffered - 3);
            Compact();
            var read = await _stream.ReadAsync(_buffer.AsMemory(_end), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return new HeadReadResult(_end == _start ? HeadReadStatus.EndOfStream : HeadReadStatus.Truncated, default);
            }

            _end += read;
        }
    }

    /// <summary>
    /// Copies one body to <paramref name="destination"/>, exactly as framed. For <see cref="BodyKind.CloseDelimited"/>
    /// it copies until the stream ends. Flushes <paramref name="destination"/> as data arrives, so slow streams aren't held back.
    /// </summary>
    public async ValueTask CopyBodyAsync(BodyFraming framing, Stream destination, int maxTrailerBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        switch (framing.Kind)
        {
            case BodyKind.None:
                return;
            case BodyKind.ContentLength:
                await CopyExactAsync(framing.Length, destination, cancellationToken).ConfigureAwait(false);
                return;
            case BodyKind.Chunked:
                await CopyChunkedAsync(destination, maxTrailerBytes, cancellationToken).ConfigureAwait(false);
                return;
            case BodyKind.CloseDelimited:
                await CopyToEndAsync(destination, cancellationToken).ConfigureAwait(false);
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(framing));
        }
    }

    /// <summary>Copies the buffered bytes, then everything else until the stream ends. Used for tunnels and close-delimited bodies.</summary>
    public async ValueTask CopyToEndAsync(Stream destination, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        while (true)
        {
            if (_end > _start)
            {
                await WriteAsync(destination, _buffer.AsMemory(_start, _end - _start), cancellationToken).ConfigureAwait(false);
                _start = _end;
            }

            _start = _end = 0;
            var read = await _stream.ReadAsync(_buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return;
            }

            _end = read;
        }
    }

    /// <summary>Returns the buffer to the pool.</summary>
    public void Dispose()
    {
        if (_buffer.Length == 0)
        {
            return;
        }

        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = [];
        _start = _end = 0;
    }

    /// <summary>
    /// Finds the end of a head: the earliest LF followed by CRLF or by another LF.
    /// Returns the length of the head including the blank line, or 0 when it isn't complete yet.
    /// </summary>
    internal static int FindHeadEnd(ReadOnlySpan<byte> data, int from)
    {
        while (true)
        {
            var offset = data[from..].IndexOf((byte)'\n');
            if (offset < 0)
            {
                return 0;
            }

            var lf = from + offset;
            if (lf + 1 >= data.Length)
            {
                return 0; // Need the next byte to decide.
            }

            if (data[lf + 1] == '\n')
            {
                return lf + 2;
            }

            if (data[lf + 1] == '\r')
            {
                if (lf + 2 >= data.Length)
                {
                    return 0;
                }

                if (data[lf + 2] == '\n')
                {
                    return lf + 3;
                }
            }

            from = lf + 1;
        }
    }

    private async ValueTask CopyExactAsync(long remaining, Stream destination, CancellationToken cancellationToken)
    {
        while (remaining > 0)
        {
            if (_end == _start)
            {
                await FillAsync(cancellationToken).ConfigureAwait(false);
            }

            var take = (int)Math.Min(remaining, _end - _start);
            await WriteAsync(destination, _buffer.AsMemory(_start, take), cancellationToken).ConfigureAwait(false);
            _start += take;
            remaining -= take;
        }
    }

    private async ValueTask CopyChunkedAsync(Stream destination, int maxTrailerBytes, CancellationToken cancellationToken)
    {
        while (true)
        {
            var lineLength = await ReadLineAsync(MaxChunkLineBytes, cancellationToken).ConfigureAwait(false);
            var size = ParseChunkSize(_buffer.AsSpan(_start, lineLength - 2));
            await WriteAsync(destination, _buffer.AsMemory(_start, lineLength), cancellationToken).ConfigureAwait(false);
            _start += lineLength;

            if (size == 0)
            {
                break;
            }

            await CopyExactAsync(size, destination, cancellationToken).ConfigureAwait(false);
            var crlf = await ReadLineAsync(2, cancellationToken).ConfigureAwait(false);
            if (crlf != 2)
            {
                throw new HttpFramingException("Chunk data wasn't followed by CRLF.");
            }

            await WriteAsync(destination, _buffer.AsMemory(_start, 2), cancellationToken).ConfigureAwait(false);
            _start += 2;
        }

        // Trailer section: field lines, then an empty line.
        var trailerBytes = 0;
        while (true)
        {
            var lineLength = await ReadLineAsync(MaxChunkLineBytes, cancellationToken).ConfigureAwait(false);
            trailerBytes += lineLength;
            if (trailerBytes > maxTrailerBytes)
            {
                throw new HttpFramingException("The trailer section is too large.");
            }

            var isLast = lineLength == 2;
            if (!isLast && !IsTrailerLine(_buffer.AsSpan(_start, lineLength - 2)))
            {
                throw new HttpFramingException("A trailer line is malformed.");
            }

            await WriteAsync(destination, _buffer.AsMemory(_start, lineLength), cancellationToken).ConfigureAwait(false);
            _start += lineLength;
            if (isLast)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Ensures a whole CRLF-terminated line is buffered at <see cref="_start"/> and returns its length including the CRLF.
    /// A bare CR or bare LF throws.
    /// </summary>
    private async ValueTask<int> ReadLineAsync(int maxLineBytes, CancellationToken cancellationToken)
    {
        var scanned = 0;
        while (true)
        {
            var span = _buffer.AsSpan(_start, _end - _start);
            var lf = span[scanned..].IndexOf((byte)'\n');
            if (lf >= 0)
            {
                lf += scanned;
                if (lf == 0 || span[lf - 1] != '\r' || span[..(lf - 1)].Contains((byte)'\r'))
                {
                    throw new HttpFramingException("A chunked line didn't end in CRLF.");
                }

                if (lf + 1 > maxLineBytes)
                {
                    throw new HttpFramingException("A chunked line is too long.");
                }

                return lf + 1;
            }

            if (span.Length >= maxLineBytes)
            {
                throw new HttpFramingException("A chunked line is too long.");
            }

            scanned = span.Length;
            await FillAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Parses a chunk-size line without its CRLF: 1 to 15 hex digits, then optional whitespace and extensions.</summary>
    internal static long ParseChunkSize(ReadOnlySpan<byte> line)
    {
        long size = 0;
        var digits = 0;
        while (digits < line.Length && Uri.IsHexDigit((char)line[digits]))
        {
            if (++digits > 15)
            {
                throw new HttpFramingException("A chunk size is too large.");
            }

            var c = line[digits - 1];
            size = (size << 4) | (long)(c <= '9' ? c - '0' : (c | 0x20) - 'a' + 10);
        }

        if (digits == 0)
        {
            throw new HttpFramingException("A chunk size is missing.");
        }

        var rest = line[digits..];
        var i = 0;
        while (i < rest.Length && rest[i] is (byte)' ' or (byte)'\t')
        {
            i++;
        }

        if (i < rest.Length && rest[i] != ';')
        {
            throw new HttpFramingException("A chunk-size line is malformed.");
        }

        if (i == rest.Length && i > 0)
        {
            throw new HttpFramingException("A chunk-size line has trailing whitespace.");
        }

        foreach (var b in rest[i..])
        {
            if (b is not ((byte)'\t' or >= 0x20 and <= 0x7E))
            {
                throw new HttpFramingException("A chunk extension contains a character that isn't allowed.");
            }
        }

        return size;
    }

    private static bool IsTrailerLine(ReadOnlySpan<byte> line)
    {
        var colon = line.IndexOf((byte)':');
        return colon > 0 && HeadParser.IsToken(line[..colon]) && HeadParser.IsFieldValue(line[(colon + 1)..]);
    }

    private async ValueTask FillAsync(CancellationToken cancellationToken)
    {
        Compact();
        if (_end == _buffer.Length)
        {
            throw new HttpFramingException("The read buffer is full.");
        }

        var read = await _stream.ReadAsync(_buffer.AsMemory(_end), cancellationToken).ConfigureAwait(false);
        if (read == 0)
        {
            throw new EndOfStreamException("The peer closed the connection part way through a body.");
        }

        _end += read;
    }

    private void Compact()
    {
        if (_start == 0)
        {
            return;
        }

        var length = _end - _start;
        if (length > 0)
        {
            Buffer.BlockCopy(_buffer, _start, _buffer, 0, length);
        }

        _start = 0;
        _end = length;
    }

    private static async ValueTask WriteAsync(Stream destination, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        await destination.WriteAsync(data, cancellationToken).ConfigureAwait(false);
        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
