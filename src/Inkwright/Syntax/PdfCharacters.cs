using System.Buffers;

namespace Inkwright.Syntax;

/// <summary>
/// The character classes of ISO 32000-1 §7.2.2, exposed as <see cref="SearchValues{T}"/> so that
/// scanning a token is a vectorised search rather than a byte-at-a-time loop.
/// </summary>
internal static class PdfCharacters
{
    /// <summary>The six bytes PDF treats as white space.</summary>
    internal static readonly SearchValues<byte> Whitespace =
        SearchValues.Create([0x00, 0x09, 0x0A, 0x0C, 0x0D, 0x20]);

    /// <summary>The bytes that terminate a regular token.</summary>
    internal static readonly SearchValues<byte> Delimiters =
        SearchValues.Create("()<>[]{}/%"u8);

    /// <summary>White space and delimiters together: everything that ends a regular token.</summary>
    internal static readonly SearchValues<byte> TokenTerminators =
        SearchValues.Create([0x00, 0x09, 0x0A, 0x0C, 0x0D, 0x20,
            (byte)'(', (byte)')', (byte)'<', (byte)'>', (byte)'[', (byte)']',
            (byte)'{', (byte)'}', (byte)'/', (byte)'%']);

    /// <summary>The bytes that can appear in a numeric token.</summary>
    internal static readonly SearchValues<byte> NumberStart =
        SearchValues.Create("+-.0123456789"u8);

    internal static bool IsWhitespace(byte b) => b is 0x00 or 0x09 or 0x0A or 0x0C or 0x0D or 0x20;

    internal static bool IsDelimiter(byte b) =>
        b is (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']'
            or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';

    internal static bool IsRegular(byte b) => !IsWhitespace(b) && !IsDelimiter(b);

    internal static bool IsEndOfLine(byte b) => b is 0x0A or 0x0D;

    internal static int HexValue(byte b) => b switch
    {
        >= (byte)'0' and <= (byte)'9' => b - (byte)'0',
        >= (byte)'a' and <= (byte)'f' => b - (byte)'a' + 10,
        >= (byte)'A' and <= (byte)'F' => b - (byte)'A' + 10,
        _ => -1,
    };
}
