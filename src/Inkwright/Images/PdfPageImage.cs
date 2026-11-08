using Inkwright.Content;
using Inkwright.Cos;

namespace Inkwright.Images;

/// <summary>
/// An image a page draws, as found in its resources. The pixels are decoded on demand, because a
/// caller often wants to know what images a document holds long before it wants any of them.
/// </summary>
/// <param name="Name">The resource name the content stream draws it by.</param>
/// <param name="Stream">The image XObject.</param>
/// <param name="Resources">The resources its colour space may be named in.</param>
public readonly record struct PdfPageImage(string Name, PdfStream Stream, PdfDictionary Resources)
{
    /// <summary>The width in samples.</summary>
    public int Width => (int)Stream.Dictionary.GetInteger(PdfNames.Width);

    /// <summary>The height in samples.</summary>
    public int Height => (int)Stream.Dictionary.GetInteger(PdfNames.Height);

    /// <summary>The codec the data is in, or <see langword="null"/> when it decoded to plain samples.</summary>
    public string? Codec => Stream.UndecodedFilter?.Value;

    /// <summary>Whether the image is a stencil rather than a picture.</summary>
    public bool IsStencil => Stream.Dictionary.Get(PdfNames.ImageMask).AsBoolean;

    /// <summary>Whether this library can turn the image into pixels.</summary>
    public bool CanDecode => PdfImageReader.CanDecode(Stream);

    /// <summary>Decodes the image.</summary>
    /// <exception cref="NotSupportedException">The image uses a codec this library does not decode.</exception>
    public PdfImageData Decode() => PdfImageReader.Decode(Stream, Resources);

    /// <inheritdoc />
    public override string ToString() => $"{Name}: {Width} by {Height}{(Codec is null ? string.Empty : $" ({Codec})")}";
}

/// <summary>An invocation of an image on a page, with its visible top-left page-space bounds.</summary>
/// <param name="Image">The image resource being painted.</param>
/// <param name="Bounds">Its axis-aligned bounds in points from the page's top-left corner.</param>
/// <param name="MarkedContentId">The enclosing structure-content id, when present.</param>
public readonly record struct PdfPlacedImage(
    PdfPageImage Image,
    PdfRectangle Bounds,
    int? MarkedContentId);

/// <summary>Finds the images a page draws, following the forms it draws them through.</summary>
internal static class PageImageFinder
{
    /// <summary>How deep forms may nest before the search gives up.</summary>
    private const int MaxDepth = 12;

    internal static List<PdfPageImage> Find(PdfPage page)
    {
        List<PdfPageImage> images = [];
        var visited = new HashSet<PdfStream>();

        Collect(page.Resources, images, visited, 0);
        return images;
    }

    private static void Collect(PdfDictionary resources, List<PdfPageImage> images, HashSet<PdfStream> visited, int depth)
    {
        if (depth > MaxDepth || resources.GetDictionary(PdfNames.XObject) is not { } objects)
            return;

        foreach (KeyValuePair<PdfName, PdfValue> entry in objects)
        {
            if (objects.Get(entry.Key).AsStream is not { } stream || !visited.Add(stream))
                continue;

            PdfName? subtype = stream.Dictionary.GetName(PdfNames.Subtype);

            if (subtype == PdfNames.Image)
            {
                images.Add(new PdfPageImage(entry.Key.Value, stream, resources));
                continue;
            }

            // A form draws images of its own, through its own resources or the page's.
            if (subtype == PdfNames.Form)
                Collect(stream.Dictionary.GetDictionary(PdfNames.Resources) ?? resources, images, visited, depth + 1);
        }
    }
}

/// <summary>One invocation that paints an image, associated with its enclosing MCID when present.</summary>
internal readonly record struct PdfImageOccurrence(
    PdfPageImage Image,
    int? MarkedContentId,
    PdfObjectId MarkedContentStream,
    PdfMatrix Transform);

/// <summary>Walks page and form content streams in paint order to locate image invocations.</summary>
internal static class PageImageOccurrenceFinder
{
    private const int MaxDepth = 12;

    internal static IReadOnlyList<PdfImageOccurrence> Find(PdfPage page, bool skipHiddenLayers)
    {
        List<PdfImageOccurrence> occurrences = [];
        Run(
            page.GetContent(),
            page.Resources,
            page.Document,
            PdfMatrix.Identity,
            inheritedContext: null,
            contentStream: PdfObjectId.None,
            hidden: false,
            skipHiddenLayers ? page.Document.OptionalContent : null,
            occurrences,
            depth: 0);
        return occurrences;
    }

    private static void Run(
        ReadOnlySpan<byte> content,
        PdfDictionary resources,
        PdfDocument document,
        PdfMatrix initialTransform,
        MarkedContentContext? inheritedContext,
        PdfObjectId contentStream,
        bool hidden,
        PdfOptionalContent? layers,
        List<PdfImageOccurrence> occurrences,
        int depth)
    {
        if (depth > MaxDepth)
            return;

        ContentOperations operations = ContentOperations.Parse(content, document);
        var transforms = new Stack<PdfMatrix>();
        var marked = new Stack<(MarkedContentContext? Context, bool Hidden)>();
        PdfMatrix transform = initialTransform;
        MarkedContentContext? currentContext = inheritedContext;

        for (int index = 0; index < operations.Count; index++)
        {
            ReadOnlySpan<PdfValue> operands = operations.GetOperands(index);
            switch (operations.GetOperator(index))
            {
                case PdfOperator.SaveState:
                    transforms.Push(transform);
                    break;

                case PdfOperator.RestoreState:
                    if (transforms.Count > 0)
                        transform = transforms.Pop();
                    break;

                case PdfOperator.ConcatMatrix when operands.Length >= 6:
                    transform = Matrix(operands) * transform;
                    break;

                case PdfOperator.BeginMarkedContent:
                case PdfOperator.BeginMarkedContentProperties:
                    marked.Push((currentContext, hidden));
                    if (operations.GetOperator(index) == PdfOperator.BeginMarkedContentProperties &&
                        operands.Length >= 2)
                    {
                        PdfValue properties = Resolve(operands[1], resources);
                        if (properties.AsDictionary?.Get(PdfNames.MCID) is
                            { Kind: PdfValueKind.Integer } number &&
                            number.AsInteger is >= 0 and <= int.MaxValue)
                        {
                            currentContext = new MarkedContentContext((int)number.AsInteger, contentStream);
                        }

                        if (!hidden && layers is not null && operands[0].AsName == PdfNames.OC &&
                            !layers.IsVisible(Raw(operands[1], resources)))
                        {
                            hidden = true;
                        }
                    }
                    break;

                case PdfOperator.EndMarkedContent:
                    if (marked.Count > 0)
                        (currentContext, hidden) = marked.Pop();
                    break;

                case PdfOperator.DrawXObject when operands.Length >= 1 && operands[0].AsName is { } name:
                    DrawXObject(
                        name,
                        resources,
                        document,
                        transform,
                        currentContext,
                        hidden,
                        layers,
                        occurrences,
                        depth);
                    break;

                case PdfOperator.InlineImage when operands.Length >= 1 && operands[^1].AsStream is { } inline:
                    if (!hidden)
                    {
                        occurrences.Add(new PdfImageOccurrence(
                            new PdfPageImage("inline", inline, resources),
                            currentContext?.MarkedContentId,
                            currentContext?.Stream ?? PdfObjectId.None,
                            transform));
                    }
                    break;
            }
        }
    }

    private static void DrawXObject(
        PdfName name,
        PdfDictionary resources,
        PdfDocument document,
        PdfMatrix transform,
        MarkedContentContext? context,
        bool hidden,
        PdfOptionalContent? layers,
        List<PdfImageOccurrence> occurrences,
        int depth)
    {
        PdfDictionary? xObjects = resources.GetDictionary(PdfNames.XObject);
        PdfValue rawObject = xObjects?[name] ?? PdfValue.Null;
        if (xObjects?.GetStream(name) is not { } stream)
            return;

        PdfName? subtype = stream.Dictionary.GetName(PdfNames.Subtype);
        if (!hidden && layers is not null && stream.Dictionary[PdfNames.OC] is { IsNull: false } group &&
            !layers.IsVisible(group))
        {
            hidden = true;
        }

        if (subtype == PdfNames.Image)
        {
            if (!hidden)
            {
                occurrences.Add(new PdfImageOccurrence(
                    new PdfPageImage(name.Value, stream, resources),
                    context?.MarkedContentId,
                    context?.Stream ?? PdfObjectId.None,
                    transform));
            }
            return;
        }

        if (subtype != PdfNames.Form)
            return;

        PdfDictionary formResources = stream.Dictionary.GetDictionary(PdfNames.Resources) ?? resources;
        PdfMatrix formTransform = PdfMatrix.FromArray(stream.Dictionary.GetArray(PdfNames.Matrix)) * transform;
        Run(
            stream.DecodedSpan(),
            formResources,
            document,
            formTransform,
            context,
            rawObject.IsReference ? rawObject.AsReference : PdfObjectId.None,
            hidden,
            layers,
            occurrences,
            depth + 1);
    }

    private static PdfValue Resolve(PdfValue operand, PdfDictionary resources) =>
        operand.AsName is { } named
            ? resources.GetDictionary(PdfName.Get("Properties"))?.Get(named) ?? PdfValue.Null
            : operand;

    private static PdfValue Raw(PdfValue operand, PdfDictionary resources) =>
        operand.AsName is { } named
            ? resources.GetDictionary(PdfName.Get("Properties"))?[named] ?? PdfValue.Null
            : operand;

    private static PdfMatrix Matrix(ReadOnlySpan<PdfValue> operands) => new(
        operands[0].AsNumber,
        operands[1].AsNumber,
        operands[2].AsNumber,
        operands[3].AsNumber,
        operands[4].AsNumber,
        operands[5].AsNumber);

    private readonly record struct MarkedContentContext(int MarkedContentId, PdfObjectId Stream);
}
