using System.Buffers;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace Inkwright.Filters;

/// <summary>
/// The <c>FlateDecode</c> filter of ISO 32000-1 §7.4.4. Decoding is deliberately forgiving:
/// producers ship streams with stray leading bytes, missing zlib wrappers and truncated tails, and
/// every one of those still yields the bytes decoded so far rather than an exception.
/// </summary>
public static class FlateFilter
{
    /// <summary>Inflates a stream payload.</summary>
    /// <param name="data">The compressed bytes.</param>
    /// <returns>The inflated bytes, truncated at the first unrecoverable position.</returns>
    public static byte[] Decode(ReadOnlySpan<byte> data)
        => Decode(data.ToArray(), PdfFilterLimits.Cap, strict: false, out _, out _);

    /// <summary>Inflates no more than <paramref name="maxDecodedBytes"/> bytes.</summary>
    internal static byte[] Decode(
        ReadOnlyMemory<byte> data,
        int maxDecodedBytes,
        out bool limitExceeded,
        out bool invalidData) =>
        Decode(data, maxDecodedBytes, strict: true, out limitExceeded, out invalidData);

    /// <summary>Runs the tolerant decoder under an explicit output bound.</summary>
    internal static byte[] DecodeCompatible(
        ReadOnlyMemory<byte> data,
        int maxDecodedBytes,
        out bool limitExceeded,
        out bool invalidData) =>
        Decode(data, maxDecodedBytes, strict: false, out limitExceeded, out invalidData);

    private static byte[] Decode(
        ReadOnlyMemory<byte> data,
        int maxDecodedBytes,
        bool strict,
        out bool limitExceeded,
        out bool invalidData)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxDecodedBytes);
        limitExceeded = false;
        invalidData = false;

        ReadOnlyMemory<byte> body;
        if (strict)
        {
            // Recursive PDF/A validation must not reinterpret a damaged stream by skipping
            // arbitrary leading bytes. The forgiving public decoder retains that compatibility.
            body = data;
        }
        else
        {
            int start = 0;
            while (start < data.Length && IsWhitespace(data.Span[start]))
                start++;

            if (start >= data.Length)
                return [];

            body = data[start..];
        }

        // A well-formed payload carries a zlib header; the check byte pair is a multiple of 31.
        bool looksLikeZlib = body.Length >= 2 &&
                             (body.Span[0] & 0x0F) == 8 &&
                             ((body.Span[0] << 8) + body.Span[1]) % 31 == 0;

        if (strict)
        {
            bool hasStrictHeader = body.Length >= 6 &&
                                   looksLikeZlib &&
                                   (body.Span[0] >> 4) <= 7 &&
                                   (body.Span[1] & 0x20) == 0;
            if (!hasStrictHeader)
            {
                invalidData = true;
                return [];
            }

            bool success = TryInflate(
                body,
                zlib: true,
                maxDecodedBytes,
                strict: true,
                out byte[] strictResult,
                out limitExceeded,
                out invalidData);
            if (!success)
                invalidData = true;
            return strictResult;
        }

        if (looksLikeZlib && TryInflate(
                body, zlib: true, maxDecodedBytes, strict: false,
                out byte[] inflated, out limitExceeded, out _))
            return inflated;

        if (TryInflate(
                body, zlib: false, maxDecodedBytes, strict: false,
                out byte[] raw, out limitExceeded, out _) && raw.Length > 0)
            return raw;

        if (!looksLikeZlib && TryInflate(
                body, zlib: true, maxDecodedBytes, strict: false,
                out byte[] zlibAnyway, out limitExceeded, out _) &&
            zlibAnyway.Length > 0)
            return zlibAnyway;

        // Some producers prepend a byte of noise before an otherwise valid stream.
        if (body.Length > 1 && TryInflate(
                body[1..], zlib: true, maxDecodedBytes, strict: false,
                out byte[] shifted, out limitExceeded, out _) &&
            shifted.Length > 0)
            return shifted;

        limitExceeded = false;
        return raw.Length > 0 ? raw : [];
    }

    /// <summary>Deflates bytes into a zlib-wrapped payload.</summary>
    /// <param name="data">The plaintext bytes.</param>
    /// <param name="level">How hard to work at shrinking the output.</param>
    public static byte[] Encode(ReadOnlySpan<byte> data, CompressionLevel level = CompressionLevel.Optimal)
    {
        using var output = new MemoryStream(Math.Max(64, data.Length / 3));
        using (var deflate = new ZLibStream(output, level, leaveOpen: true))
            deflate.Write(data);

        return output.ToArray();
    }

    private static bool TryInflate(
        ReadOnlyMemory<byte> body,
        bool zlib,
        int cap,
        bool strict,
        out byte[] result,
        out bool limitExceeded,
        out bool invalidData)
    {
        limitExceeded = false;
        invalidData = false;
        int initialCapacity = Math.Min(cap, Math.Max(1, Math.Min(body.Length, 64 * 1024)));
        var writer = new ArrayBufferWriter<byte>(initialCapacity);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);

        try
        {
            using Stream input = strict && zlib
                ? new TailExactReadStream(body)
                : Input(body);
            using Stream decompressor = zlib
                ? new ZLibStream(input, CompressionMode.Decompress)
                : new DeflateStream(input, CompressionMode.Decompress);

            while (true)
            {
                int read;
                try
                {
                    read = decompressor.Read(buffer, 0, buffer.Length);
                }
                catch (InvalidDataException)
                {
                    // Truncated or damaged tail: keep whatever decoded cleanly.
                    result = writer.WrittenSpan.ToArray();
                    invalidData = strict;
                    return result.Length > 0;
                }

                if (read == 0)
                    break;

                if (read > cap - writer.WrittenCount)
                {
                    // A decompression bomb: keep what fits and stop rather than exhaust memory.
                    writer.Write(buffer.AsSpan(0, cap - writer.WrittenCount));
                    limitExceeded = true;
                    break;
                }

                writer.Write(buffer.AsSpan(0, read));
            }

            result = writer.WrittenSpan.ToArray();
            if (strict && zlib && !limitExceeded)
            {
                // ZLibStream may otherwise read ahead through bytes appended after the Adler-32
                // trailer. TailExactReadStream makes the tail bytewise so Position identifies the
                // exact end consumed by the zlib member.
                if (input.Position != input.Length || !HasValidAdler32(body.Span, result))
                    invalidData = true;
            }
            return true;
        }
        catch (InvalidDataException)
        {
            result = writer.WrittenSpan.ToArray();
            invalidData = strict;
            return result.Length > 0;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static bool HasValidAdler32(ReadOnlySpan<byte> encoded, ReadOnlySpan<byte> decoded)
    {
        if (encoded.Length < 6)
            return false;

        uint expected = ((uint)encoded[^4] << 24) |
                        ((uint)encoded[^3] << 16) |
                        ((uint)encoded[^2] << 8) |
                        encoded[^1];
        const uint modulus = 65_521;
        uint first = 1;
        uint second = 0;
        int position = 0;
        while (position < decoded.Length)
        {
            int end = Math.Min(decoded.Length, position + 5_552);
            for (; position < end; position++)
            {
                first += decoded[position];
                second += first;
            }

            first %= modulus;
            second %= modulus;
        }

        return expected == (second << 16 | first);
    }

    private static MemoryStream Input(ReadOnlyMemory<byte> data)
    {
        if (MemoryMarshal.TryGetArray(data, out ArraySegment<byte> segment))
        {
            return new MemoryStream(
                segment.Array!,
                segment.Offset,
                segment.Count,
                writable: false,
                publiclyVisible: true);
        }

        return new MemoryStream(data.ToArray(), writable: false);
    }

    /// <summary>
    /// Supplies most input normally, then limits the final eight bytes to one byte per read. That
    /// prevents the framework inflater from buffering an appended suffix together with the zlib
    /// trailer and lets the strict caller verify exact stream consumption through Position.
    /// </summary>
    private sealed class TailExactReadStream(ReadOnlyMemory<byte> data) : Stream
    {
        private readonly ReadOnlyMemory<byte> _data = data;
        private readonly int _tailStart = Math.Max(0, data.Length - 8);
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _data.Length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            int remaining = _data.Length - _position;
            if (remaining <= 0 || buffer.IsEmpty)
                return 0;

            int allowed = _position < _tailStart
                ? Math.Min(buffer.Length, _tailStart - _position)
                : 1;
            int count = Math.Min(remaining, allowed);
            _data.Span.Slice(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }

        public override int ReadByte()
        {
            if (_position >= _data.Length)
                return -1;

            return _data.Span[_position++];
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static bool IsWhitespace(byte b) => b is 0x00 or 0x09 or 0x0A or 0x0C or 0x0D or 0x20;
}
