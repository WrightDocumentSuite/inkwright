using System.Buffers;
using System.Globalization;

namespace Inkwright.IO;

/// <summary>
/// A buffered byte writer that knows how to spell PDF's scalars. Numbers go straight to UTF-8
/// through <see cref="IUtf8SpanFormattable"/>, so serialising a document never builds a string.
/// </summary>
internal sealed class Utf8Writer : IDisposable
{
    private const int DefaultCapacity = 64 * 1024;

    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private byte[] _buffer;
    private int _position;

    internal Utf8Writer(Stream stream, bool leaveOpen = true, int capacity = DefaultCapacity, long startOffset = 0)
    {
        _stream = stream;
        _leaveOpen = leaveOpen;
        _buffer = ArrayPool<byte>.Shared.Rent(Math.Max(4096, capacity));
        Length = startOffset;
    }

    /// <summary>How many bytes have been handed to the writer, flushed or not.</summary>
    internal long Length { get; private set; }

    internal void Write(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= _buffer.Length)
        {
            FlushBuffer();
            _stream.Write(bytes);
            Length += bytes.Length;
            return;
        }

        if (_position + bytes.Length > _buffer.Length)
            FlushBuffer();

        bytes.CopyTo(_buffer.AsSpan(_position));
        _position += bytes.Length;
        Length += bytes.Length;
    }

    internal void WriteByte(byte value)
    {
        if (_position == _buffer.Length)
            FlushBuffer();

        _buffer[_position++] = value;
        Length++;
    }

    internal void WriteSpace() => WriteByte((byte)' ');

    internal void WriteNewLine() => WriteByte((byte)'\n');

    internal void Write(long value)
    {
        Span<byte> digits = stackalloc byte[20];
        if (value.TryFormat(digits, out int written, default, CultureInfo.InvariantCulture))
            Write(digits[..written]);
    }

    /// <summary>
    /// Writes a real the way PDF wants it: no exponent, no trailing zeros, and integral values
    /// spelled as integers so that the common case stays short.
    /// </summary>
    internal void Write(double value)
    {
        if (!double.IsFinite(value))
        {
            Write("0"u8);
            return;
        }

        if (double.IsInteger(value) && Math.Abs(value) < 1e15)
        {
            Write((long)value);
            return;
        }

        Span<byte> digits = stackalloc byte[32];
        if (value.TryFormat(digits, out int written, "0.######", CultureInfo.InvariantCulture))
            Write(digits[..written]);
        else
            Write("0"u8);
    }

    /// <summary>Writes an integer padded with leading zeros, the shape a cross-reference entry needs.</summary>
    internal void WritePadded(long value, int width)
    {
        Span<byte> digits = stackalloc byte[20];
        if (!value.TryFormat(digits, out int written, default, CultureInfo.InvariantCulture))
            return;

        for (int i = written; i < width; i++)
            WriteByte((byte)'0');

        Write(digits[..written]);
    }

    internal void Flush()
    {
        FlushBuffer();
        _stream.Flush();
    }

    public void Dispose()
    {
        if (_buffer.Length == 0)
            return;

        FlushBuffer();
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = [];

        if (!_leaveOpen)
            _stream.Dispose();
    }

    private void FlushBuffer()
    {
        if (_position == 0)
            return;

        _stream.Write(_buffer, 0, _position);
        _position = 0;
    }
}
