using PdfSharp.Fonts;

namespace Inkwright.Benchmarks;

/// <summary>
/// Gives PDFsharp somewhere to find a font. The core package ships no font lookup of its own, so a
/// comparison has to supply one; the file it points at is the same one Inkwright embeds, which
/// keeps the two sides drawing the same glyphs.
/// </summary>
internal sealed class SystemFontResolver : IFontResolver
{
    private static readonly string[] Candidates =
    [
        @"C:\Windows\Fonts\arial.ttf",
        "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
        "/Library/Fonts/Arial.ttf",
        "/System/Library/Fonts/Supplemental/Arial.ttf",
    ];

    internal static string? FontPath { get; } = System.Array.Find(Candidates, File.Exists);

    public byte[]? GetFont(string faceName) => FontPath is null ? null : File.ReadAllBytes(FontPath);

    public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic) =>
        FontPath is null ? null : new FontResolverInfo("Benchmark");
}
