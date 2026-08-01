using System.Globalization;

namespace Inkwright.Cos;

/// <summary>
/// Identity of an indirect object: its object number and generation number (ISO 32000-1 §7.3.10).
/// </summary>
/// <param name="Number">The object number. Positive for real objects; zero marks the free-list head.</param>
/// <param name="Generation">The generation number, normally zero.</param>
public readonly record struct PdfObjectId(int Number, int Generation) : IComparable<PdfObjectId>
{
    /// <summary>The identity used for "no object", equivalent to <c>default</c>.</summary>
    public static PdfObjectId None => default;

    /// <summary>Whether this identity refers to an actual object rather than being the default.</summary>
    public bool IsValid => Number > 0;

    /// <inheritdoc />
    public int CompareTo(PdfObjectId other)
    {
        int byNumber = Number.CompareTo(other.Number);
        return byNumber != 0 ? byNumber : Generation.CompareTo(other.Generation);
    }

    /// <inheritdoc />
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Number} {Generation} R");
}
