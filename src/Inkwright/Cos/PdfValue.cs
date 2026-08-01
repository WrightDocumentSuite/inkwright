using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Inkwright.Cos;

/// <summary>
/// A PDF object of any type, stored as a 24-byte tagged union: scalars live inline and never
/// allocate, composites carry a reference. <c>default(PdfValue)</c> is the null object.
/// </summary>
public readonly struct PdfValue : IEquatable<PdfValue>
{
    private readonly object? _obj;
    private readonly long _bits;
    private readonly PdfValueKind _kind;

    private PdfValue(PdfValueKind kind, long bits, object? obj)
    {
        _kind = kind;
        _bits = bits;
        _obj = obj;
    }

    /// <summary>The null object.</summary>
    public static PdfValue Null => default;

    /// <summary>The boolean object <c>true</c>.</summary>
    public static PdfValue True { get; } = new(PdfValueKind.Boolean, 1, null);

    /// <summary>The boolean object <c>false</c>.</summary>
    public static PdfValue False { get; } = new(PdfValueKind.Boolean, 0, null);

    /// <summary>Which of the PDF object types this value holds.</summary>
    public PdfValueKind Kind => _kind;

    /// <summary>Whether this is the null object.</summary>
    public bool IsNull => _kind == PdfValueKind.Null;

    /// <summary>Whether this is an integer or a real.</summary>
    public bool IsNumber => _kind is PdfValueKind.Integer or PdfValueKind.Real;

    /// <summary>Whether this value is an indirect reference that still needs resolving.</summary>
    public bool IsReference => _kind == PdfValueKind.Reference;

    /// <summary>Creates a boolean object.</summary>
    /// <param name="value">The boolean payload.</param>
    public static PdfValue Boolean(bool value) => value ? True : False;

    /// <summary>Creates an integer object.</summary>
    /// <param name="value">The integer payload.</param>
    public static PdfValue Integer(long value) => new(PdfValueKind.Integer, value, null);

    /// <summary>Creates a real object.</summary>
    /// <param name="value">The real payload; non-finite values are rejected.</param>
    public static PdfValue Real(double value)
    {
        if (!double.IsFinite(value))
            throw new ArgumentOutOfRangeException(nameof(value), value, "PDF real numbers must be finite.");

        return new PdfValue(PdfValueKind.Real, BitConverter.DoubleToInt64Bits(value), null);
    }

    /// <summary>Creates a number, choosing the integer representation when the value is integral.</summary>
    /// <param name="value">The numeric payload.</param>
    public static PdfValue Number(double value) =>
        double.IsInteger(value) && value is >= long.MinValue and <= long.MaxValue
            ? Integer((long)value)
            : Real(value);

    /// <summary>Creates a string object from raw bytes.</summary>
    /// <param name="bytes">The already-unescaped bytes; the array is stored, not copied.</param>
    /// <param name="hex">Whether the file spelled the string in hexadecimal form.</param>
    public static PdfValue String(byte[] bytes, bool hex = false)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return new PdfValue(PdfValueKind.String, hex ? 1 : 0, bytes);
    }

    /// <summary>Creates a text string object, encoded per ISO 32000-1 §7.9.2.2.</summary>
    /// <param name="text">The text to encode as PDFDocEncoding when possible, UTF-16BE otherwise.</param>
    public static PdfValue TextString(string text) => String(PdfTextEncoding.EncodeTextString(text));

    /// <summary>Creates a name object.</summary>
    /// <param name="name">The name instance.</param>
    public static PdfValue Name(PdfName name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return new PdfValue(PdfValueKind.Name, 0, name);
    }

    /// <summary>Creates a name object from its decoded text.</summary>
    /// <param name="name">The name without its leading solidus.</param>
    public static PdfValue Name(string name) => Name(PdfName.Get(name));

    /// <summary>Wraps an array object.</summary>
    /// <param name="array">The array instance.</param>
    public static PdfValue Array(PdfArray array)
    {
        ArgumentNullException.ThrowIfNull(array);
        return new PdfValue(PdfValueKind.Array, 0, array);
    }

    /// <summary>Wraps a dictionary object.</summary>
    /// <param name="dictionary">The dictionary instance.</param>
    public static PdfValue Dictionary(PdfDictionary dictionary)
    {
        ArgumentNullException.ThrowIfNull(dictionary);
        return new PdfValue(PdfValueKind.Dictionary, 0, dictionary);
    }

    /// <summary>Wraps a stream object.</summary>
    /// <param name="stream">The stream instance.</param>
    public static PdfValue Stream(PdfStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return new PdfValue(PdfValueKind.Stream, 0, stream);
    }

    /// <summary>Creates an indirect reference.</summary>
    /// <param name="id">The identity of the referenced object.</param>
    public static PdfValue Reference(PdfObjectId id) =>
        new(PdfValueKind.Reference, ((long)(uint)id.Number << 32) | (uint)id.Generation, null);

    /// <summary>Creates an indirect reference.</summary>
    /// <param name="number">The object number.</param>
    /// <param name="generation">The generation number.</param>
    public static PdfValue Reference(int number, int generation = 0) =>
        Reference(new PdfObjectId(number, generation));

    /// <summary>Converts a boolean into a PDF object.</summary>
    /// <param name="value">The boolean payload.</param>
    public static implicit operator PdfValue(bool value) => Boolean(value);

    /// <summary>Converts an integer into a PDF object.</summary>
    /// <param name="value">The integer payload.</param>
    public static implicit operator PdfValue(long value) => Integer(value);

    /// <summary>Converts an integer into a PDF object.</summary>
    /// <param name="value">The integer payload.</param>
    public static implicit operator PdfValue(int value) => Integer(value);

    /// <summary>Converts a real into a PDF object, collapsing integral values to integers.</summary>
    /// <param name="value">The numeric payload.</param>
    public static implicit operator PdfValue(double value) => Number(value);

    /// <summary>Converts a name into a PDF object.</summary>
    /// <param name="name">The name instance.</param>
    public static implicit operator PdfValue(PdfName name) => Name(name);

    /// <summary>Converts an array into a PDF object.</summary>
    /// <param name="array">The array instance.</param>
    public static implicit operator PdfValue(PdfArray array) => Array(array);

    /// <summary>Converts a dictionary into a PDF object.</summary>
    /// <param name="dictionary">The dictionary instance.</param>
    public static implicit operator PdfValue(PdfDictionary dictionary) => Dictionary(dictionary);

    /// <summary>Converts a stream into a PDF object.</summary>
    /// <param name="stream">The stream instance.</param>
    public static implicit operator PdfValue(PdfStream stream) => Stream(stream);

    /// <summary>The boolean payload; false for every other kind.</summary>
    public bool AsBoolean => _kind == PdfValueKind.Boolean && _bits != 0;

    /// <summary>The numeric payload as an integer; zero for non-numeric kinds.</summary>
    public long AsInteger => _kind switch
    {
        PdfValueKind.Integer => _bits,
        PdfValueKind.Real => (long)BitConverter.Int64BitsToDouble(_bits),
        _ => 0,
    };

    /// <summary>The numeric payload as a real; zero for non-numeric kinds.</summary>
    public double AsNumber => _kind switch
    {
        PdfValueKind.Integer => _bits,
        PdfValueKind.Real => BitConverter.Int64BitsToDouble(_bits),
        _ => 0d,
    };

    /// <summary>The raw bytes of a string object; empty for every other kind.</summary>
    public ReadOnlySpan<byte> AsStringBytes =>
        _kind == PdfValueKind.String ? Unsafe.As<byte[]>(_obj!) : [];

    /// <summary>Whether a string object was written in hexadecimal form in the source file.</summary>
    public bool IsHexString => _kind == PdfValueKind.String && _bits != 0;

    /// <summary>The name payload, or <see langword="null"/> for every other kind.</summary>
    public PdfName? AsName => _kind == PdfValueKind.Name ? Unsafe.As<PdfName>(_obj!) : null;

    /// <summary>The array payload, or <see langword="null"/> for every other kind.</summary>
    public PdfArray? AsArray => _kind == PdfValueKind.Array ? Unsafe.As<PdfArray>(_obj!) : null;

    /// <summary>
    /// The dictionary payload. Streams answer with their own dictionary, since every stream is a
    /// dictionary with data attached.
    /// </summary>
    public PdfDictionary? AsDictionary => _kind switch
    {
        PdfValueKind.Dictionary => Unsafe.As<PdfDictionary>(_obj!),
        PdfValueKind.Stream => Unsafe.As<PdfStream>(_obj!).Dictionary,
        _ => null,
    };

    /// <summary>The stream payload, or <see langword="null"/> for every other kind.</summary>
    public PdfStream? AsStream => _kind == PdfValueKind.Stream ? Unsafe.As<PdfStream>(_obj!) : null;

    /// <summary>The identity carried by an indirect reference; <see cref="PdfObjectId.None"/> otherwise.</summary>
    public PdfObjectId AsReference => _kind == PdfValueKind.Reference
        ? new PdfObjectId((int)(_bits >> 32), (int)(_bits & 0xFFFFFFFF))
        : PdfObjectId.None;

    /// <summary>Decodes a string object as text per ISO 32000-1 §7.9.2.2.</summary>
    /// <returns>The decoded text, or <see langword="null"/> when this is not a string.</returns>
    public string? AsTextString =>
        _kind == PdfValueKind.String ? PdfTextEncoding.DecodeTextString(AsStringBytes) : null;

    /// <summary>Reads a numeric value, reporting whether the object actually was a number.</summary>
    /// <param name="value">Receives the number, or zero.</param>
    public bool TryGetNumber(out double value)
    {
        value = AsNumber;
        return IsNumber;
    }

    /// <summary>Reads an integer value, reporting whether the object actually was numeric.</summary>
    /// <param name="value">Receives the integer, or zero.</param>
    public bool TryGetInteger(out long value)
    {
        value = AsInteger;
        return IsNumber;
    }

    /// <summary>Reads the array payload.</summary>
    /// <param name="array">Receives the array when present.</param>
    public bool TryGetArray([NotNullWhen(true)] out PdfArray? array)
    {
        array = AsArray;
        return array is not null;
    }

    /// <summary>Reads the dictionary payload, including the dictionary of a stream.</summary>
    /// <param name="dictionary">Receives the dictionary when present.</param>
    public bool TryGetDictionary([NotNullWhen(true)] out PdfDictionary? dictionary)
    {
        dictionary = AsDictionary;
        return dictionary is not null;
    }

    /// <inheritdoc />
    public bool Equals(PdfValue other)
    {
        if (_kind != other._kind)
            return false;

        return _kind switch
        {
            PdfValueKind.Null => true,
            PdfValueKind.Boolean or PdfValueKind.Integer or PdfValueKind.Real or PdfValueKind.Reference =>
                _bits == other._bits,
            PdfValueKind.String => AsStringBytes.SequenceEqual(other.AsStringBytes),
            PdfValueKind.Name => AsName!.Equals(other.AsName),
            _ => ReferenceEquals(_obj, other._obj),
        };
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is PdfValue other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => _kind switch
    {
        PdfValueKind.Null => 0,
        PdfValueKind.Boolean or PdfValueKind.Integer or PdfValueKind.Real or PdfValueKind.Reference =>
            HashCode.Combine(_kind, _bits),
        PdfValueKind.String => Marvin(AsStringBytes),
        PdfValueKind.Name => AsName!.GetHashCode(),
        _ => RuntimeHelpers.GetHashCode(_obj),
    };

    /// <summary>Compares two objects by value for scalars and by identity for composites.</summary>
    public static bool operator ==(PdfValue left, PdfValue right) => left.Equals(right);

    /// <summary>Compares two objects by value for scalars and by identity for composites.</summary>
    public static bool operator !=(PdfValue left, PdfValue right) => !left.Equals(right);

    /// <inheritdoc />
    public override string ToString() => _kind switch
    {
        PdfValueKind.Null => "null",
        PdfValueKind.Boolean => AsBoolean ? "true" : "false",
        PdfValueKind.Integer => AsInteger.ToString(CultureInfo.InvariantCulture),
        PdfValueKind.Real => AsNumber.ToString("R", CultureInfo.InvariantCulture),
        PdfValueKind.String => $"({AsTextString})",
        PdfValueKind.Name => AsName!.ToString(),
        PdfValueKind.Array => $"[{AsArray!.Count} items]",
        PdfValueKind.Dictionary => $"<<{AsDictionary!.Count} entries>>",
        PdfValueKind.Stream => $"stream({AsStream!.RawLength} bytes)",
        PdfValueKind.Reference => AsReference.ToString(),
        _ => "?",
    };

    private static int Marvin(ReadOnlySpan<byte> data)
    {
        var hash = new HashCode();
        hash.AddBytes(data);
        return hash.ToHashCode();
    }
}
