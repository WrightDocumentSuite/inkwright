using System.Globalization;

namespace Inkwright.IO;

/// <summary>
/// A PDF version number as it appears in the file header and the catalog's <c>Version</c> entry.
/// </summary>
/// <param name="Major">The major version, always 1 or 2 in practice.</param>
/// <param name="Minor">The minor version.</param>
public readonly record struct PdfVersion(int Major, int Minor) : IComparable<PdfVersion>
{
    /// <summary>PDF 1.4, the level most viewers treat as a safe floor.</summary>
    public static PdfVersion Pdf14 => new(1, 4);

    /// <summary>PDF 1.5, which introduced object and cross-reference streams.</summary>
    public static PdfVersion Pdf15 => new(1, 5);

    /// <summary>PDF 1.6.</summary>
    public static PdfVersion Pdf16 => new(1, 6);

    /// <summary>PDF 1.7, the version standardised as ISO 32000-1.</summary>
    public static PdfVersion Pdf17 => new(1, 7);

    /// <summary>PDF 2.0, standardised as ISO 32000-2:2020.</summary>
    public static PdfVersion Pdf20 => new(2, 0);

    /// <summary>Parses a version written as <c>1.7</c>, with or without a <c>%PDF-</c> prefix.</summary>
    /// <param name="text">The text to parse.</param>
    /// <param name="version">Receives the parsed version.</param>
    public static bool TryParse(ReadOnlySpan<char> text, out PdfVersion version)
    {
        version = default;

        if (text.StartsWith("%PDF-", StringComparison.Ordinal))
            text = text[5..];

        int dot = text.IndexOf('.');
        if (dot <= 0)
            return false;

        if (!int.TryParse(text[..dot], NumberStyles.None, CultureInfo.InvariantCulture, out int major))
            return false;

        ReadOnlySpan<char> minorText = text[(dot + 1)..];
        int digits = 0;
        while (digits < minorText.Length && char.IsAsciiDigit(minorText[digits]))
            digits++;

        if (digits == 0 || !int.TryParse(minorText[..digits], NumberStyles.None, CultureInfo.InvariantCulture, out int minor))
            return false;

        version = new PdfVersion(major, minor);
        return true;
    }

    /// <inheritdoc />
    public int CompareTo(PdfVersion other)
    {
        int byMajor = Major.CompareTo(other.Major);
        return byMajor != 0 ? byMajor : Minor.CompareTo(other.Minor);
    }

    /// <summary>Whether the left version is at least the right one.</summary>
    public static bool operator >=(PdfVersion left, PdfVersion right) => left.CompareTo(right) >= 0;

    /// <summary>Whether the left version is at most the right one.</summary>
    public static bool operator <=(PdfVersion left, PdfVersion right) => left.CompareTo(right) <= 0;

    /// <summary>Whether the left version is later than the right one.</summary>
    public static bool operator >(PdfVersion left, PdfVersion right) => left.CompareTo(right) > 0;

    /// <summary>Whether the left version is earlier than the right one.</summary>
    public static bool operator <(PdfVersion left, PdfVersion right) => left.CompareTo(right) < 0;

    /// <summary>The later of two versions.</summary>
    /// <param name="left">The first version.</param>
    /// <param name="right">The second version.</param>
    public static PdfVersion Max(PdfVersion left, PdfVersion right) => left >= right ? left : right;

    /// <inheritdoc />
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}");
}
