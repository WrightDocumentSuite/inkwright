using System.Text;
using Inkwright.Content;
using Inkwright.Layout.Elements;

namespace Inkwright.Layout;

/// <summary>
/// A document described as content rather than as pages. The renderer lays the content out twice:
/// once to count the pages, once to draw them, which is what lets a footer say "page 3 of 12"
/// without the caller knowing the total in advance.
/// </summary>
public sealed class Report
{
    private const int MaxPages = 20_000;

    private readonly List<ReportSection> _sections = [];

    /// <summary>Starts a new report.</summary>
    public static Report Create() => new();

    /// <summary>Adds a section: a run of pages sharing a size, margins, header and footer.</summary>
    /// <param name="configure">Describes the section.</param>
    public Report Page(Action<PageDescriptor> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var section = new ReportSection();
        configure(new PageDescriptor(section));
        _sections.Add(section);
        return this;
    }

    /// <summary>Renders the report into an existing document, appending to whatever is already there.</summary>
    /// <param name="document">The document to draw into.</param>
    public void Render(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        bool tagged = _sections.Exists(section => section.Tagged);
        Tagging.StructureTreeBuilder? builder = null;
        Tagging.StructureElement? root = null;

        if (tagged)
        {
            builder = new Tagging.StructureTreeBuilder(document)
            {
                Language = _sections.Find(section => section.Language is not null)?.Language,
            };

            root = builder.AddRoot("Document");
        }

        var bookmarks = new List<LayoutContext.BookmarkRequest>();

        foreach (ReportSection section in _sections)
            RenderSection(document, section, builder, root, bookmarks);

        builder?.Build();
        BuildOutline(document, bookmarks);
    }

    /// <summary>
    /// Turns the bookmarks the content declared into a document outline, nesting each by the depth
    /// it asked for. A bookmark that claims to sit deeper than the tree currently reaches attaches
    /// to the deepest entry there is, which is the sensible reading of a level that skipped a step.
    /// </summary>
    private static void BuildOutline(PdfDocument document, List<LayoutContext.BookmarkRequest> bookmarks)
    {
        if (bookmarks.Count == 0)
            return;

        PdfOutline outline = PdfOutline.OpenOrCreate(document);
        List<PdfOutlineItem> stack = [];

        foreach (LayoutContext.BookmarkRequest request in bookmarks)
        {
            // A tagged section can be pointed at by structure, which survives reflowing and is what
            // ISO 14289-2 §8.8 asks of a destination that stays inside the document.
            PdfDestination destination = request.Element is { Id.IsValid: true } element
                ? PdfDestination.ToStructureElement(element, top: request.Top)
                : PdfDestination.XyzLocation(request.Page, top: request.Top);

            int level = Math.Clamp(request.Level, 0, stack.Count);
            PdfOutlineItem item = level == 0
                ? outline.Add(request.Title, destination)
                : stack[level - 1].Add(request.Title, destination);

            stack.RemoveRange(level, stack.Count - level);
            stack.Add(item);
        }
    }

    /// <summary>Renders the report into a fresh document.</summary>
    public PdfDocument ToDocument()
    {
        var document = PdfDocument.Create();
        Render(document);
        return document;
    }

    /// <summary>Renders the report and returns the file.</summary>
    public byte[] ToArray()
    {
        using PdfDocument document = ToDocument();
        return document.ToArray();
    }

    /// <summary>Renders the report and writes it to disk.</summary>
    /// <param name="path">Where to write the file.</param>
    public void Save(string path)
    {
        using PdfDocument document = ToDocument();
        document.Save(path);
    }

    private static void RenderSection(
        PdfDocument document,
        ReportSection section,
        Tagging.StructureTreeBuilder? builder,
        Tagging.StructureElement? root,
        List<LayoutContext.BookmarkRequest> bookmarks)
    {
        var pages = new PageContext();
        var context = new LayoutContext(document, pages) { DefaultTextStyle = section.DefaultTextStyle };

        // The counting pass draws nothing: elements advance their state with no canvas attached, and
        // tagging stays off so the structure tree is built exactly once.
        section.Reset(context);
        int total = Run(document, section, context, draw: false);

        pages.Reset();
        pages.TotalPages = total;
        section.Reset(context);

        if (section.Tagged && builder is not null && root is not null)
            context.Tagging = new TaggingSession(builder, root);

        Run(document, section, context, draw: true);
        context.Tagging = null;

        // Bookmarks are collected on the drawing pass only, so they are read off once it is done.
        bookmarks.AddRange(context.Bookmarks);
    }

    private static int Run(PdfDocument document, ReportSection section, LayoutContext context, bool draw)
    {
        PageContext pages = context.Pages;
        int count = 0;

        while (count < MaxPages)
        {
            count++;
            pages.CurrentPage = count;
            pages.ContentStartedOnPage = false;

            PdfPage? page = null;
            ContentCanvas? canvas = null;

            if (draw)
            {
                page = document.Pages.Add(section.Size);
                if (context.Tagging is { } tagging)
                    tagging.Page = page;

                context.CurrentPage = page;
                context.CurrentPageHeight = section.Size.Height;

                canvas = ContentCanvas.ForPage(page);

                // Layout works top-down, so the page matrix flips y once and everything else follows.
                canvas.Transform(new PdfMatrix(1, 0, 0, -1, 0, section.Size.Height));

                if (section.Background is { } background)
                {
                    canvas.Save();
                    canvas.FillColor(background).Rectangle(0, 0, section.Size.Width, section.Size.Height).Fill();
                    canvas.Restore();
                }

                context.Canvas = new LayoutCanvas(canvas);
            }
            else
            {
                context.Canvas = null;
            }

            bool progressed = DrawPage(section, context);

            if (draw)
            {
                canvas!.Commit(append: false);
                canvas.Dispose();
            }

            if (!section.Content.HasMoreContent)
                break;

            if (!progressed)
            {
                throw new LayoutException(
                    $"Nothing fitted on page {count}, so the layout would never finish. " +
                    "An element is asking for more space than a page can give.")
                {
                    ElementTrace = Trace(section.Content, 0),
                };
            }
        }

        context.Canvas = null;
        return count;
    }

    private static bool DrawPage(ReportSection section, LayoutContext context)
    {
        double width = section.Size.Width - section.Margin.Horizontal;
        double height = section.Size.Height - section.Margin.Vertical;

        if (width <= 0 || height <= 0)
            throw new LayoutException("The margins leave no room for content.");

        section.Header.Reset();
        section.Footer.Reset();

        var full = new LayoutSize(width, height);
        double headerHeight = Height(section.Header.Measure(full, context));
        double footerHeight = Height(section.Footer.Measure(full, context));
        double contentHeight = height - headerHeight - footerHeight;

        if (contentHeight <= 0)
            throw new LayoutException("The header and footer leave no room for content.");

        LayoutCanvas? canvas = context.Canvas;

        if (canvas is not null && headerHeight > 0)
        {
            canvas.Save().Translate(section.Margin.Left, section.Margin.Top);
            section.Header.Draw(new LayoutSize(width, headerHeight), context);
            canvas.Restore();
        }
        else
        {
            section.Header.Draw(new LayoutSize(width, headerHeight), context);
        }

        if (canvas is not null && footerHeight > 0)
        {
            canvas.Save().Translate(section.Margin.Left, section.Size.Height - section.Margin.Bottom - footerHeight);
            section.Footer.Draw(new LayoutSize(width, footerHeight), context);
            canvas.Restore();
        }
        else
        {
            section.Footer.Draw(new LayoutSize(width, footerHeight), context);
        }

        var area = new LayoutSize(width, contentHeight);
        SpacePlan plan = section.Content.Measure(area, context);

        if (canvas is not null)
        {
            canvas.Save().Translate(section.Margin.Left, section.Margin.Top + headerHeight);
            section.Content.Draw(area, context);
            canvas.Restore();
        }
        else
        {
            section.Content.Draw(area, context);
        }

        return plan.DrawsSomething;
    }

    private static double Height(SpacePlan plan) => plan.Kind == SpacePlanKind.Wrap ? 0 : plan.Height;

    private static string Trace(LayoutElement element, int depth)
    {
        var text = new StringBuilder();
        text.Append(' ', depth * 2)
            .Append(element.DebugName ?? element.GetType().Name)
            .Append('\n');

        if (depth < 24)
        {
            foreach (LayoutElement child in element.Children)
                text.Append(Trace(child, depth + 1));
        }

        return text.ToString();
    }
}

/// <summary>One run of pages sharing a size, margins, header and footer.</summary>
internal sealed class ReportSection
{
    internal PageSize Size { get; set; } = PageSize.A4;

    internal Insets Margin { get; set; } = Insets.All(40);

    internal PdfColor? Background { get; set; }

    internal TextStyle DefaultTextStyle { get; set; } = TextStyle.Default;

    internal bool Tagged { get; set; }

    internal string? Language { get; set; }

    internal Container Header { get; } = new();

    internal Container Content { get; } = new();

    internal Container Footer { get; } = new();

    internal void Reset(LayoutContext context)
    {
        context.DefaultTextStyle = DefaultTextStyle;
        Header.Reset();
        Content.Reset();
        Footer.Reset();
        ApplyDefaultStyle(Header);
        ApplyDefaultStyle(Content);
        ApplyDefaultStyle(Footer);
    }

    private void ApplyDefaultStyle(LayoutElement element)
    {
        // Text that never chose a style inherits the section's, which is how a document-wide font
        // setting reaches a paragraph nobody styled.
        if (element is TextElement text && text.BaseStyle == TextStyle.Default)
            text.BaseStyle = DefaultTextStyle;

        foreach (LayoutElement child in element.Children)
            ApplyDefaultStyle(child);
    }
}

/// <summary>Describes a run of pages.</summary>
public sealed class PageDescriptor
{
    private readonly ReportSection _section;

    internal PageDescriptor(ReportSection section) => _section = section;

    /// <summary>Sets the page size.</summary>
    /// <param name="size">The size.</param>
    public void Size(PageSize size) => _section.Size = size;

    /// <summary>Sets the same margin on every side.</summary>
    /// <param name="all">The margin in points.</param>
    public void Margin(double all) => _section.Margin = Insets.All(all);

    /// <summary>Sets the margins edge by edge.</summary>
    /// <param name="left">The left margin.</param>
    /// <param name="top">The top margin.</param>
    /// <param name="right">The right margin.</param>
    /// <param name="bottom">The bottom margin.</param>
    public void Margin(double left, double top, double right, double bottom) =>
        _section.Margin = new Insets(left, top, right, bottom);

    /// <summary>Paints a colour across the whole page.</summary>
    /// <param name="color">The fill colour.</param>
    public void Background(PdfColor color) => _section.Background = color;

    /// <summary>Sets the style text inherits when it chooses none.</summary>
    /// <param name="style">The style.</param>
    public void DefaultTextStyle(TextStyle style) => _section.DefaultTextStyle = style;

    /// <summary>Adjusts the style text inherits.</summary>
    /// <param name="configure">Produces the new style from the current one.</param>
    public void DefaultTextStyle(Func<TextStyle, TextStyle> configure) =>
        _section.DefaultTextStyle = configure(_section.DefaultTextStyle);

    /// <summary>
    /// Builds a logical structure tree while drawing, which is what makes the result readable by
    /// assistive technology and a prerequisite for the accessible PDF/A levels.
    /// </summary>
    /// <param name="language">The document language as a BCP 47 tag, such as <c>en-GB</c>.</param>
    public void Tagged(string? language = null)
    {
        _section.Tagged = true;
        _section.Language = language;
    }

    /// <summary>The slot drawn at the top of every page in the section.</summary>
    public Container Header() => _section.Header;

    /// <summary>The slot holding the content that flows across pages.</summary>
    public Container Content() => _section.Content;

    /// <summary>The slot drawn at the bottom of every page in the section.</summary>
    public Container Footer() => _section.Footer;
}
