namespace Inkwright.Fonts;

/// <summary>
/// The fonts a document draws with. Asking for the same font twice returns the same instance, so a
/// document that uses Helvetica on a thousand pages still holds one font dictionary.
/// </summary>
public sealed class PdfFontCollection
{
    private readonly PdfDocument _document;
    private readonly Dictionary<string, PdfFont> _byKey = new(StringComparer.Ordinal);

    internal PdfFontCollection(PdfDocument document) => _document = document;

    /// <summary>Helvetica, the default sans-serif face every reader carries.</summary>
    public PdfFont Helvetica => Standard(Standard14Font.Helvetica);

    /// <summary>Helvetica Bold.</summary>
    public PdfFont HelveticaBold => Standard(Standard14Font.HelveticaBold);

    /// <summary>Times Roman.</summary>
    public PdfFont TimesRoman => Standard(Standard14Font.TimesRoman);

    /// <summary>Courier, the default monospaced face.</summary>
    public PdfFont Courier => Standard(Standard14Font.Courier);

    /// <summary>One of the fourteen built-in fonts.</summary>
    /// <param name="font">The font's metrics.</param>
    public PdfFont Standard(Standard14Font font)
    {
        ArgumentNullException.ThrowIfNull(font);

        string key = "std:" + font.Name;
        if (_byKey.TryGetValue(key, out PdfFont? existing))
            return existing;

        var created = new Standard14PdfFont(_document, font);
        _byKey[key] = created;
        return created;
    }

    /// <summary>One of the fourteen built-in fonts, by name.</summary>
    /// <param name="name">A standard font name or a common substitute for one.</param>
    /// <exception cref="ArgumentException">The name is not one of the fourteen.</exception>
    public PdfFont Standard(string name)
    {
        Standard14Font metrics = Standard14Font.Find(name)
            ?? throw new ArgumentException($"'{name}' is not one of the standard 14 fonts.", nameof(name));

        return Standard(metrics);
    }

    /// <summary>Embeds a TrueType or OpenType font from a file.</summary>
    /// <param name="path">The font file.</param>
    /// <param name="subset">Whether to ship only the glyphs the document uses.</param>
    public PdfFont Embed(string path, bool subset = true)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        string key = "file:" + Path.GetFullPath(path) + (subset ? ":subset" : string.Empty);
        if (_byKey.TryGetValue(key, out PdfFont? existing))
            return existing;

        var created = new EmbeddedTrueTypeFont(_document, TrueTypeFont.Load(path), subset);
        _byKey[key] = created;
        return created;
    }

    /// <summary>Embeds a TrueType or OpenType font from memory.</summary>
    /// <param name="data">The font file.</param>
    /// <param name="subset">Whether to ship only the glyphs the document uses.</param>
    public PdfFont Embed(byte[] data, bool subset = true)
    {
        ArgumentNullException.ThrowIfNull(data);
        return Embed(TrueTypeFont.Parse(data), subset);
    }

    /// <summary>Embeds an already-parsed font program.</summary>
    /// <param name="font">The font program.</param>
    /// <param name="subset">Whether to ship only the glyphs the document uses.</param>
    public PdfFont Embed(TrueTypeFont font, bool subset = true)
    {
        ArgumentNullException.ThrowIfNull(font);

        string key = "ttf:" + font.PostScriptName + ":" + font.Data.Length + (subset ? ":subset" : string.Empty);
        if (_byKey.TryGetValue(key, out PdfFont? existing))
            return existing;

        var created = new EmbeddedTrueTypeFont(_document, font, subset);
        _byKey[key] = created;
        return created;
    }

    /// <summary>
    /// Embeds a font installed on this machine, looked up by family name. Returns
    /// <see langword="null"/> rather than guessing when nothing matches.
    /// </summary>
    /// <param name="family">The family name, such as <c>Segoe UI</c>.</param>
    /// <param name="bold">Whether to prefer a bold face.</param>
    /// <param name="italic">Whether to prefer an italic face.</param>
    public PdfFont? EmbedSystem(string family, bool bold = false, bool italic = false)
    {
        string? path = SystemFonts.Find(family, bold, italic);
        return path is null ? null : Embed(path);
    }
}
