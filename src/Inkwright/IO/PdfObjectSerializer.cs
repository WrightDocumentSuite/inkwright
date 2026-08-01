using Inkwright.Cos;
using Inkwright.Security;

namespace Inkwright.IO;

/// <summary>
/// Turns an object graph back into PDF syntax. Strings are written literally while they stay
/// readable and switch to hexadecimal once they do not, which keeps generated files diffable
/// without ever risking an escaping bug on binary data.
/// </summary>
internal sealed class PdfObjectSerializer(Utf8Writer writer)
{
    private const int MaxDepth = 96;

    private readonly Utf8Writer _writer = writer;

    internal StandardSecurityHandler? Encryption { get; set; }

    internal PdfObjectId CurrentObject { get; set; }

    internal void WriteValue(PdfValue value) => WriteValue(value, 0);

    /// <summary>Writes a complete indirect object, header and <c>endobj</c> included.</summary>
    /// <param name="id">The identity to give the object.</param>
    /// <param name="value">The object body.</param>
    internal void WriteIndirectObject(PdfObjectId id, PdfValue value)
    {
        CurrentObject = id;

        _writer.Write(id.Number);
        _writer.WriteSpace();
        _writer.Write(id.Generation);
        _writer.Write(" obj\n"u8);
        WriteValue(value, 0);
        _writer.Write("\nendobj\n"u8);
    }

    private void WriteValue(PdfValue value, int depth)
    {
        if (depth > MaxDepth)
        {
            _writer.Write("null"u8);
            return;
        }

        switch (value.Kind)
        {
            case PdfValueKind.Null:
                _writer.Write("null"u8);
                break;

            case PdfValueKind.Boolean:
                _writer.Write(value.AsBoolean ? "true"u8 : "false"u8);
                break;

            case PdfValueKind.Integer:
                _writer.Write(value.AsInteger);
                break;

            case PdfValueKind.Real:
                _writer.Write(value.AsNumber);
                break;

            case PdfValueKind.String:
                WriteString(value.AsStringBytes);
                break;

            case PdfValueKind.Name:
                WriteName(value.AsName!);
                break;

            case PdfValueKind.Array:
                WriteArray(value.AsArray!, depth);
                break;

            case PdfValueKind.Dictionary:
                WriteDictionary(value.AsDictionary!, depth);
                break;

            case PdfValueKind.Stream:
                WriteStream(value.AsStream!, depth);
                break;

            case PdfValueKind.Reference:
                PdfObjectId id = value.AsReference;
                _writer.Write(id.Number);
                _writer.WriteSpace();
                _writer.Write(id.Generation);
                _writer.Write(" R"u8);
                break;

            default:
                _writer.Write("null"u8);
                break;
        }
    }

    private void WriteArray(PdfArray array, int depth)
    {
        _writer.WriteByte((byte)'[');

        ReadOnlySpan<PdfValue> items = array.AsSpan();
        for (int i = 0; i < items.Length; i++)
        {
            if (i > 0)
                _writer.WriteSpace();

            WriteValue(items[i], depth + 1);
        }

        _writer.WriteByte((byte)']');
    }

    private void WriteDictionary(PdfDictionary dictionary, int depth)
    {
        _writer.Write("<<"u8);

        // The signed blob of a signature is exempt from encryption (§7.6.2): it has to be readable
        // by a verifier that has not been given the password, and it is covered by the byte range
        // rather than by the document key.
        bool signature = Encryption is not null && IsSignature(dictionary);

        foreach (KeyValuePair<PdfName, PdfValue> entry in dictionary)
        {
            WriteName(entry.Key);

            // A name or a number needs no separator after the key's own delimiter.
            if (entry.Value.Kind is not (PdfValueKind.Name or PdfValueKind.Array or PdfValueKind.Dictionary
                or PdfValueKind.Stream or PdfValueKind.String))
            {
                _writer.WriteSpace();
            }

            bool exempt = signature && entry.Key == PdfNames.Contents;
            if (!exempt)
            {
                WriteValue(entry.Value, depth + 1);
                continue;
            }

            StandardSecurityHandler? handler = Encryption;
            Encryption = null;
            WriteValue(entry.Value, depth + 1);
            Encryption = handler;
        }

        _writer.Write(">>"u8);
    }

    private static bool IsSignature(PdfDictionary dictionary)
    {
        if (dictionary.GetName(PdfNames.Type) is { } type)
            return type.Value is "Sig" or "DocTimeStamp";

        // Some producers leave the type out; a byte range beside the contents says the same thing.
        return dictionary.ContainsKey(PdfNames.ByteRange) && dictionary.ContainsKey(PdfNames.Contents);
    }

    private void WriteStream(PdfStream stream, int depth)
    {
        ReadOnlySpan<byte> payload = stream.RawData.Span;
        byte[]? encrypted = null;

        // A document that says its metadata is in the clear has to mean it: leaving the XMP readable
        // is the entire point of the option, and indexers read it without ever knowing the password.
        bool exempt = Encryption is { EncryptMetadata: false } &&
                      stream.Dictionary.GetName(PdfNames.Type)?.Value == "Metadata";

        if (Encryption is not null && !exempt)
        {
            encrypted = Encryption.EncryptStream(CurrentObject, payload);
            payload = encrypted;
        }

        stream.Dictionary.Set(PdfNames.Length, PdfValue.Integer(payload.Length));
        WriteDictionary(stream.Dictionary, depth);

        _writer.Write("\nstream\n"u8);
        _writer.Write(payload);
        _writer.Write("\nendstream"u8);

        if (encrypted is not null)
            stream.Dictionary.Set(PdfNames.Length, PdfValue.Integer(stream.RawLength));
    }

    private void WriteName(PdfName name)
    {
        _writer.WriteByte((byte)'/');

        ReadOnlySpan<byte> bytes = name.Utf8;
        foreach (byte b in bytes)
        {
            if (b is > 0x20 and < 0x7F && b != (byte)'#' && !Syntax.PdfCharacters.IsDelimiter(b))
            {
                _writer.WriteByte(b);
                continue;
            }

            _writer.WriteByte((byte)'#');
            _writer.WriteByte(HexDigit(b >> 4));
            _writer.WriteByte(HexDigit(b & 0x0F));
        }
    }

    private void WriteString(ReadOnlySpan<byte> bytes)
    {
        byte[]? encrypted = null;
        if (Encryption is not null)
        {
            encrypted = Encryption.EncryptString(CurrentObject, bytes);
            bytes = encrypted;
        }

        if (IsPrintable(bytes))
            WriteLiteralString(bytes);
        else
            WriteHexString(bytes);

        _ = encrypted;
    }

    private static bool IsPrintable(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > 512)
            return false;

        foreach (byte b in bytes)
        {
            if (b is < 0x20 or > 0x7E)
                return false;
        }

        return true;
    }

    private void WriteLiteralString(ReadOnlySpan<byte> bytes)
    {
        _writer.WriteByte((byte)'(');

        foreach (byte b in bytes)
        {
            if (b is (byte)'(' or (byte)')' or (byte)'\\')
                _writer.WriteByte((byte)'\\');

            _writer.WriteByte(b);
        }

        _writer.WriteByte((byte)')');
    }

    private void WriteHexString(ReadOnlySpan<byte> bytes)
    {
        _writer.WriteByte((byte)'<');

        foreach (byte b in bytes)
        {
            _writer.WriteByte(HexDigit(b >> 4));
            _writer.WriteByte(HexDigit(b & 0x0F));
        }

        _writer.WriteByte((byte)'>');
    }

    private static byte HexDigit(int value) => (byte)(value < 10 ? '0' + value : 'A' + value - 10);
}
