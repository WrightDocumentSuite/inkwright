using Inkwright.Fonts;

namespace Inkwright.Layout;

/// <summary>
/// The base of everything that can be placed on a page. Layout runs in two phases per position:
/// <see cref="Measure"/> asks what the element would do with a piece of space and changes nothing,
/// then <see cref="Draw"/> commits to it. Elements carry the state that lets them continue on the
/// next page, and <see cref="Reset"/> puts that state back for a fresh pass.
/// </summary>
public abstract class LayoutElement
{
    /// <summary>A name that shows up in overflow diagnostics.</summary>
    public string? DebugName { get; set; }

    /// <summary>The elements this one contains, for tree walks and diagnostics.</summary>
    public virtual IReadOnlyList<LayoutElement> Children => [];

    /// <summary>Reports what the element would do with the space, without drawing anything.</summary>
    /// <param name="available">The space on offer.</param>
    /// <param name="context">Fonts, page numbers and the document being built.</param>
    public abstract SpacePlan Measure(LayoutSize available, LayoutContext context);

    /// <summary>Draws the element into the space a matching <see cref="Measure"/> approved.</summary>
    /// <param name="available">The space the element was given.</param>
    /// <param name="context">The canvas to draw on, plus fonts and page numbers.</param>
    public abstract void Draw(LayoutSize available, LayoutContext context);

    /// <summary>Forgets how much has been drawn, so the element can be laid out again from the top.</summary>
    public virtual void Reset()
    {
        foreach (LayoutElement child in Children)
            child.Reset();
    }

    /// <summary>Whether the element still has content that has not been drawn.</summary>
    public virtual bool HasMoreContent => false;
}

/// <summary>
/// A slot that holds one element. The fluent API works by handing out containers: each call fills
/// the slot with a wrapper and returns the wrapper's own slot, so decorations nest naturally.
/// </summary>
public sealed class Container : LayoutElement
{
    private LayoutElement? _child;

    /// <summary>The element in the slot, if anything has been put there.</summary>
    public LayoutElement? Child
    {
        get => _child;
        set => _child = value;
    }

    /// <inheritdoc />
    public override IReadOnlyList<LayoutElement> Children => _child is null ? [] : [_child];

    /// <inheritdoc />
    public override bool HasMoreContent => _child?.HasMoreContent ?? false;

    /// <inheritdoc />
    public override SpacePlan Measure(LayoutSize available, LayoutContext context) =>
        _child?.Measure(available, context) ?? SpacePlan.Empty();

    /// <inheritdoc />
    public override void Draw(LayoutSize available, LayoutContext context) => _child?.Draw(available, context);
}

/// <summary>
/// What an element needs while it is being laid out: where to draw, which fonts to use, and what
/// the page numbers are. The same context is used for measuring, with <see cref="Canvas"/> absent.
/// </summary>
public sealed class LayoutContext
{
    private readonly Dictionary<(string Family, bool Bold, bool Italic), PdfFont> _fonts = [];

    internal LayoutContext(PdfDocument document, PageContext pages)
    {
        Document = document;
        Pages = pages;
    }

    /// <summary>The document being built.</summary>
    public PdfDocument Document { get; }

    /// <summary>Page numbering, including the totals that only the second pass knows.</summary>
    public PageContext Pages { get; }

    /// <summary>The canvas to draw on, or <see langword="null"/> while measuring.</summary>
    public LayoutCanvas? Canvas { get; internal set; }

    /// <summary>Whether this is the drawing phase rather than the measuring one.</summary>
    public bool IsDrawing => Canvas is not null;

    /// <summary>The page currently being drawn, set during the drawing pass.</summary>
    internal PdfPage? CurrentPage { get; set; }

    /// <summary>The height of the page currently being drawn, used to place bookmarks.</summary>
    internal double CurrentPageHeight { get; set; }

    /// <summary>
    /// The bookmarks the content declared while drawing, each with the depth it sits at. The
    /// renderer turns them into a document outline once the whole report is laid out.
    /// </summary>
    internal List<BookmarkRequest> Bookmarks { get; } = [];

    /// <summary>Records where a bookmark should point, captured as its content is drawn.</summary>
    /// <param name="Level">How deeply the bookmark nests, counting from zero.</param>
    /// <param name="Title">The text shown in the navigation pane.</param>
    /// <param name="Page">The page the bookmarked content landed on.</param>
    /// <param name="Top">The distance from the page's bottom to the content's top, in PDF units.</param>
    /// <param name="Element">
    /// The structure element the bookmarked content produced, when the section is tagged. It is
    /// what lets the outline entry be a structure destination, which ISO 14289-2 §8.8 requires of
    /// anything pointing inside the document.
    /// </param>
    internal readonly record struct BookmarkRequest(
        int Level, string Title, PdfPage Page, double Top, Tagging.StructureElement? Element = null);

    /// <summary>The style text inherits when it sets nothing of its own.</summary>
    public TextStyle DefaultTextStyle { get; internal set; } = TextStyle.Default;

    /// <summary>The structure tree being built, or <see langword="null"/> when tagging is off.</summary>
    public TaggingSession? Tagging { get; internal set; }

    /// <summary>
    /// Resolves the font a style asks for, embedding a system font when the family is not one of
    /// the built-in fourteen and falling back to Helvetica when nothing matches.
    /// </summary>
    /// <param name="style">The style whose font is wanted.</param>
    public PdfFont ResolveFont(TextStyle style)
    {
        ArgumentNullException.ThrowIfNull(style);

        if (style.Font is { } explicitFont)
            return explicitFont;

        string family = style.FontFamily ?? "Helvetica";
        var key = (family, style.Bold, style.Italic);

        if (_fonts.TryGetValue(key, out PdfFont? cached))
            return cached;

        PdfFont resolved = Resolve(family, style.Bold, style.Italic);
        _fonts[key] = resolved;
        return resolved;
    }

    private PdfFont Resolve(string family, bool bold, bool italic)
    {
        if (StandardName(family, bold, italic) is { } standard && Standard14Font.Find(standard) is { } metrics)
            return Document.Fonts.Standard(metrics);

        if (Document.Fonts.EmbedSystem(family, bold, italic) is { } system)
            return system;

        return Document.Fonts.Standard(Standard14Font.Find(StandardName("Helvetica", bold, italic)!)!);
    }

    private static string? StandardName(string family, bool bold, bool italic)
    {
        string? baseName = family switch
        {
            "Helvetica" or "Arial" or "sans-serif" => "Helvetica",
            "Times" or "Times-Roman" or "Times New Roman" or "serif" => "Times",
            "Courier" or "Courier New" or "monospace" => "Courier",
            "Symbol" => "Symbol",
            "ZapfDingbats" => "ZapfDingbats",
            _ => null,
        };

        return baseName switch
        {
            null => null,
            "Symbol" or "ZapfDingbats" => baseName,
            "Times" => (bold, italic) switch
            {
                (true, true) => "Times-BoldItalic",
                (true, false) => "Times-Bold",
                (false, true) => "Times-Italic",
                _ => "Times-Roman",
            },
            _ => (bold, italic) switch
            {
                (true, true) => baseName + "-BoldOblique",
                (true, false) => baseName + "-Bold",
                (false, true) => baseName + "-Oblique",
                _ => baseName,
            },
        };
    }
}

/// <summary>
/// Page numbering shared across a render. The first pass counts pages, so the second pass can print
/// "page 3 of 12" without anyone having to know the total in advance.
/// </summary>
public sealed class PageContext
{
    /// <summary>The page being laid out, counting from one.</summary>
    public int CurrentPage { get; internal set; } = 1;

    /// <summary>How many pages there will be, known from the second pass onwards.</summary>
    public int TotalPages { get; internal set; }

    /// <summary>Whether this is the counting pass, where <see cref="TotalPages"/> is not yet known.</summary>
    public bool IsMeasuringPass => TotalPages == 0;

    /// <summary>
    /// Whether anything has been placed on the current page yet. A page break asks this so that a
    /// break at the very top of a page does nothing instead of leaving a blank sheet behind.
    /// </summary>
    public bool ContentStartedOnPage { get; internal set; }

    internal void Reset()
    {
        CurrentPage = 1;
        ContentStartedOnPage = false;
    }
}

/// <summary>
/// Raised when content cannot be laid out: an element that never fits, or a layout that makes no
/// progress from one page to the next. The message carries the element tree so the culprit is
/// named rather than guessed at.
/// </summary>
public sealed class LayoutException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">What went wrong.</param>
    public LayoutException(string message) : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="innerException">The underlying failure.</param>
    public LayoutException(string message, Exception innerException) : base(message, innerException)
    {
    }

    /// <summary>Creates the exception.</summary>
    public LayoutException() : base("The content could not be laid out.")
    {
    }

    /// <summary>The element tree at the point of failure, indented by depth.</summary>
    public string? ElementTrace { get; init; }

    /// <inheritdoc />
    public override string ToString() =>
        ElementTrace is null ? base.ToString() : base.ToString() + "\n\nElement tree:\n" + ElementTrace;
}
