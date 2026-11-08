using Inkwright.Cos;
using Inkwright.Tagging;

namespace Inkwright;

/// <summary>
/// How a view arrives at a place in a document (ISO 32000-1 §12.3.2.2): which page, and how it is
/// fit into the window. A destination can also point at a structure element instead of a page
/// (§12.3.2.3, PDF 2.0), in which case a reader works out the page from the element's content. The
/// value object stays independent of any one document, so the same destination can be handed to a
/// bookmark, a link or an open action; it becomes a concrete array only when written.
/// </summary>
public sealed class PdfDestination
{
    private readonly PdfPage? _page;
    private readonly PdfObjectId _element;
    private readonly string _mode;
    private readonly double?[] _arguments;

    private PdfDestination(PdfPage? page, PdfObjectId element, string mode, double?[] arguments)
    {
        _page = page;
        _element = element;
        _mode = mode;
        _arguments = arguments;
    }

    /// <summary>The page the destination leads to, or nothing when it points at a structure element.</summary>
    public PdfPage? Page => _page;

    /// <summary>Whether the destination points at a structure element rather than a page.</summary>
    public bool IsStructureDestination => _element.IsValid;

    /// <summary>
    /// Shows the page at an explicit position and zoom (<c>/XYZ</c>). A null leaves that coordinate
    /// or the zoom as it is, which is what a viewer reads a null to mean.
    /// </summary>
    /// <param name="page">The page to go to.</param>
    /// <param name="left">The horizontal position to put at the left edge, or null to keep it.</param>
    /// <param name="top">The vertical position to put at the top edge, or null to keep it.</param>
    /// <param name="zoom">The zoom factor, or null to keep the current one.</param>
    public static PdfDestination XyzLocation(PdfPage page, double? left = null, double? top = null, double? zoom = null)
    {
        ArgumentNullException.ThrowIfNull(page);
        return new PdfDestination(page, default, "XYZ", [left, top, zoom]);
    }

    /// <summary>Fits the whole page in the window (<c>/Fit</c>).</summary>
    /// <param name="page">The page to go to.</param>
    public static PdfDestination FitPage(PdfPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        return new PdfDestination(page, default, "Fit", []);
    }

    /// <summary>Fits the page width, with a given row at the top (<c>/FitH</c>).</summary>
    /// <param name="page">The page to go to.</param>
    /// <param name="top">The vertical position to put at the top edge, or null to keep it.</param>
    public static PdfDestination FitWidth(PdfPage page, double? top = null)
    {
        ArgumentNullException.ThrowIfNull(page);
        return new PdfDestination(page, default, "FitH", [top]);
    }

    /// <summary>Fits the page height, with a given column at the left (<c>/FitV</c>).</summary>
    /// <param name="page">The page to go to.</param>
    /// <param name="left">The horizontal position to put at the left edge, or null to keep it.</param>
    public static PdfDestination FitHeight(PdfPage page, double? left = null)
    {
        ArgumentNullException.ThrowIfNull(page);
        return new PdfDestination(page, default, "FitV", [left]);
    }

    /// <summary>
    /// Points at a structure element rather than a page (§12.3.2.3, PDF 2.0). A reader finds the
    /// page from the element's first piece of content and then behaves as an ordinary destination
    /// would. This is what lets a link or a bookmark land on a heading no matter how the content
    /// has since reflowed.
    /// </summary>
    /// <param name="element">The structure element to go to; it must have been read as an indirect object.</param>
    /// <param name="left">The horizontal position to put at the left edge, or null to keep it.</param>
    /// <param name="top">The vertical position to put at the top edge, or null to keep it.</param>
    /// <param name="zoom">The zoom factor, or null to keep the current one.</param>
    public static PdfDestination ToStructureElement(
        PdfStructureNode element, double? left = null, double? top = null, double? zoom = null)
    {
        ArgumentNullException.ThrowIfNull(element);
        return new PdfDestination(null, Identify(element.Id, nameof(element)), "XYZ", [left, top, zoom]);
    }

    /// <summary>Fits the whole page the structure element sits on (<c>/Fit</c>).</summary>
    /// <param name="element">The structure element to go to.</param>
    public static PdfDestination ToStructureElementFit(PdfStructureNode element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return new PdfDestination(null, Identify(element.Id, nameof(element)), "Fit", []);
    }

    /// <summary>
    /// Points at an element of a structure tree being built, rather than one read back from a file.
    /// The tree has to have been written already, because only then does the element have a number
    /// a destination can name.
    /// </summary>
    /// <param name="element">The structure element to go to.</param>
    /// <param name="left">The horizontal position to put at the left edge, or null to keep it.</param>
    /// <param name="top">The vertical position to put at the top edge, or null to keep it.</param>
    /// <param name="zoom">The zoom factor, or null to keep the current one.</param>
    public static PdfDestination ToStructureElement(
        StructureElement element, double? left = null, double? top = null, double? zoom = null)
    {
        ArgumentNullException.ThrowIfNull(element);
        return new PdfDestination(null, Identify(element.Id, nameof(element)), "XYZ", [left, top, zoom]);
    }

    /// <summary>Fits the whole page an element of a tree being built sits on (<c>/Fit</c>).</summary>
    /// <param name="element">The structure element to go to.</param>
    public static PdfDestination ToStructureElementFit(StructureElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return new PdfDestination(null, Identify(element.Id, nameof(element)), "Fit", []);
    }

    private static PdfObjectId Identify(PdfObjectId id, string parameter) =>
        id.IsValid
            ? id
            : throw new ArgumentException(
                "A structure destination needs an element that is an indirect object.", parameter);

    /// <summary>
    /// Builds the explicit destination array (§12.3.2.2): the target — a page or, for a structure
    /// destination, a structure element — followed by the fit mode and its arguments.
    /// </summary>
    internal PdfArray ToArray()
    {
        var array = new PdfArray(2 + _arguments.Length);

        array.Add(PdfValue.Reference(_element.IsValid ? _element : _page!.EnsureId()));

        array.Add(PdfValue.Name(PdfName.Get(_mode)));

        foreach (double? argument in _arguments)
            array.Add(argument is { } value ? PdfValue.Real(value) : PdfValue.Null);

        return array;
    }
}
