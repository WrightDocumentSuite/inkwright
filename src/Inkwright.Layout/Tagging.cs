using Inkwright.Cos;
using Inkwright.Tagging;

namespace Inkwright.Layout;

/// <summary>
/// Builds the logical structure tree while the page is being drawn. Tagging from inside the layout
/// is the only way to get it right: the structure has to follow the order content is placed in, and
/// nothing after the fact can recover the reading order a renderer threw away.
/// </summary>
public sealed class TaggingSession
{
    private readonly Stack<StructureElement> _stack = new();

    internal TaggingSession(StructureTreeBuilder builder, StructureElement root)
    {
        Builder = builder;
        _stack.Push(root);
    }

    internal StructureTreeBuilder Builder { get; }

    /// <summary>The page currently being drawn, which marked content is attached to.</summary>
    public PdfPage? Page { get; internal set; }

    /// <summary>The node new content hangs from.</summary>
    public StructureElement Current => _stack.Peek();

    /// <summary>Opens a node that groups other nodes without holding content of its own.</summary>
    /// <param name="tag">The structure type, such as <c>Table</c> or <c>TR</c>.</param>
    /// <returns>A scope that closes the node.</returns>
    public IDisposable Group(string tag)
    {
        _stack.Push(Current.Add(tag));
        return new Scope(this, null);
    }

    /// <summary>
    /// Opens a node holding a marked-content sequence, and writes the operators that tie the two
    /// together into the page.
    /// </summary>
    /// <param name="canvas">The canvas being drawn on.</param>
    /// <param name="tag">The structure type, such as <c>P</c> or <c>Figure</c>.</param>
    /// <param name="alternateText">Text describing content that cannot be read from the glyphs.</param>
    /// <returns>A scope that closes the sequence and the node.</returns>
    public IDisposable Marked(LayoutCanvas canvas, string tag, string? alternateText = null)
    {
        ArgumentNullException.ThrowIfNull(canvas);

        StructureElement element = Current.Add(tag);
        if (!string.IsNullOrEmpty(alternateText))
            element.AlternateText = alternateText;

        if (Page is { } page)
        {
            int mcid = Builder.NextMarkedContentId(page);
            element.AddContent(page.Id, mcid);

            var properties = new PdfDictionary(1);
            properties.Set(PdfName.Get("MCID"), PdfValue.Integer(mcid));
            canvas.Content.BeginMarkedContent(PdfName.Get(tag), properties);
        }

        _stack.Push(element);
        return new Scope(this, canvas);
    }

    /// <summary>Marks drawing that carries no meaning, which a reader for the blind should skip.</summary>
    /// <param name="canvas">The canvas being drawn on.</param>
    /// <returns>A scope that closes the sequence.</returns>
    public IDisposable Artifact(LayoutCanvas canvas)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        canvas.Content.BeginMarkedContent(PdfName.Get("Artifact"));
        return new ArtifactScope(canvas);
    }

    private sealed class Scope(TaggingSession session, LayoutCanvas? canvas) : IDisposable
    {
        private bool _closed;

        public void Dispose()
        {
            if (_closed)
                return;

            _closed = true;
            session._stack.Pop();
            canvas?.Content.EndMarkedContent();
        }
    }

    private sealed class ArtifactScope(LayoutCanvas canvas) : IDisposable
    {
        private bool _closed;

        public void Dispose()
        {
            if (_closed)
                return;

            _closed = true;
            canvas.Content.EndMarkedContent();
        }
    }
}

/// <summary>A scope that does nothing, returned when tagging is switched off.</summary>
internal sealed class NullScope : IDisposable
{
    internal static NullScope Instance { get; } = new();

    public void Dispose()
    {
    }
}

/// <summary>Shorthands that keep element code free of null checks when tagging is off.</summary>
internal static class TaggingExtensions
{
    internal static IDisposable MarkedOrNothing(this LayoutContext context, string tag, string? alternateText = null) =>
        context.Tagging is { } session && context.Canvas is { } canvas
            ? session.Marked(canvas, tag, alternateText)
            : NullScope.Instance;

    internal static IDisposable GroupOrNothing(this LayoutContext context, string tag) =>
        context.Tagging is { } session ? session.Group(tag) : NullScope.Instance;
}
