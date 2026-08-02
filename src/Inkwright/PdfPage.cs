using Inkwright.Cos;
using Inkwright.IO;

namespace Inkwright;

/// <summary>
/// One page of a document. The class is a typed façade over the page dictionary: everything it
/// exposes is stored in that dictionary, and anything it does not model stays reachable through
/// <see cref="Dictionary"/>.
/// </summary>
public sealed class PdfPage
{
    private readonly PdfDocument _document;

    internal PdfPage(PdfDocument document, PdfObjectId id, PdfDictionary dictionary)
    {
        _document = document;
        Id = id;
        Dictionary = dictionary;
    }

    /// <summary>The identity of the page object in the file.</summary>
    public PdfObjectId Id { get; internal set; }

    /// <summary>The page dictionary.</summary>
    public PdfDictionary Dictionary { get; }

    /// <summary>The document the page belongs to.</summary>
    public PdfDocument Document => _document;

    /// <summary>
    /// The boundaries of the physical medium (§14.11.2). Defaults to US Letter when the file omits
    /// it, which is what viewers do.
    /// </summary>
    public PdfRectangle MediaBox
    {
        get
        {
            PdfRectangle box = PdfRectangle.FromArray(Dictionary.GetArray(PdfNames.MediaBox));
            return box.IsEmpty ? PageSize.Letter.ToRectangle() : box;
        }

        set => Set(PdfNames.MediaBox, PdfValue.Array(value.ToArray()));
    }

    /// <summary>The visible region of the page, defaulting to the media box.</summary>
    public PdfRectangle CropBox
    {
        get
        {
            PdfRectangle box = PdfRectangle.FromArray(Dictionary.GetArray(PdfNames.CropBox));
            return box.IsEmpty ? MediaBox : box.Intersect(MediaBox) is { IsEmpty: false } clipped ? clipped : MediaBox;
        }

        set => Set(PdfNames.CropBox, PdfValue.Array(value.ToArray()));
    }

    /// <summary>The region the finished page is trimmed to, defaulting to the crop box.</summary>
    public PdfRectangle TrimBox
    {
        get
        {
            PdfRectangle box = PdfRectangle.FromArray(Dictionary.GetArray(PdfNames.TrimBox));
            return box.IsEmpty ? CropBox : box;
        }

        set => Set(PdfNames.TrimBox, PdfValue.Array(value.ToArray()));
    }

    /// <summary>
    /// How far the page is turned clockwise when displayed, normalised to 0, 90, 180 or 270.
    /// </summary>
    public int Rotation
    {
        get
        {
            int rotation = (int)Dictionary.GetInteger(PdfNames.Rotate);
            rotation %= 360;
            if (rotation < 0)
                rotation += 360;

            return rotation / 90 * 90;
        }

        set
        {
            int normalized = value % 360;
            if (normalized < 0)
                normalized += 360;

            Set(PdfNames.Rotate, PdfValue.Integer(normalized / 90 * 90));
        }
    }

    /// <summary>Records that the page dictionary changed, so an incremental save writes it out.</summary>
    public void MarkChanged() => _document.MarkChanged(Id);

    private void Set(PdfName key, PdfValue value)
    {
        Dictionary.Set(key, value);
        MarkChanged();
    }

    /// <summary>The displayed width in points, after rotation.</summary>
    public double Width => Rotation is 90 or 270 ? CropBox.Height : CropBox.Width;

    /// <summary>The displayed height in points, after rotation.</summary>
    public double Height => Rotation is 90 or 270 ? CropBox.Width : CropBox.Height;

    /// <summary>The displayed size in points, after rotation.</summary>
    public PageSize Size => new(Width, Height);

    /// <summary>The resource dictionary, created on demand when the page has none.</summary>
    public PdfDictionary Resources
    {
        get
        {
            if (Dictionary.GetDictionary(PdfNames.Resources) is { } existing)
                return existing;

            var resources = new PdfDictionary { Resolver = _document };
            Set(PdfNames.Resources, PdfValue.Dictionary(resources));
            return resources;
        }
    }

    /// <summary>
    /// The annotations on the page: comments, marks, shapes, links and form widgets, in file order.
    /// </summary>
    public Annotations.PdfAnnotationCollection Annotations => field ??= new Annotations.PdfAnnotationCollection(this);

    /// <summary>
    /// The page content as one byte sequence. A page may hold an array of streams that a viewer
    /// concatenates; this joins them the same way, with a newline between parts.
    /// </summary>
    public byte[] GetContent()
    {
        PdfValue contents = Dictionary.Get(PdfNames.Contents);

        if (contents.AsStream is { } single)
            return DecodeContent(single).ToArray();

        if (contents.AsArray is not { } array)
            return [];

        // The parts are subject to the same expansion cap as a single stream, so a page cannot get
        // around the limit by splitting a decompression bomb across an array.
        long cap = Filters.PdfFilterLimits.MaxDecodedBytes;
        var buffer = new MemoryStream();

        for (int i = 0; i < array.Count && buffer.Length < cap; i++)
        {
            if (array.Get(i).AsStream is not { } part)
                continue;

            ReadOnlySpan<byte> decoded = DecodeContent(part).Span;
            if (buffer.Length + decoded.Length > cap)
                decoded = decoded[..(int)(cap - buffer.Length)];

            buffer.Write(decoded);
            buffer.WriteByte((byte)'\n');
        }

        return buffer.ToArray();
    }

    private static ReadOnlyMemory<byte> DecodeContent(PdfStream stream)
    {
        ReadOnlyMemory<byte> decoded = stream.Decode();
        if (stream.UndecodedFilter is { } filter)
        {
            throw new NotSupportedException(
                $"A page content stream uses the unsupported filter /{filter.Value}.");
        }

        return decoded;
    }

    /// <summary>Replaces the page content with a single stream.</summary>
    /// <param name="content">The content stream operators.</param>
    /// <param name="compress">Whether to deflate the stream.</param>
    public void SetContent(ReadOnlyMemory<byte> content, bool compress = true)
    {
        var stream = PdfStream.FromData(content, compress);
        PdfObjectId id = _document.AddObject(PdfValue.Stream(stream));
        Set(PdfNames.Contents, PdfValue.Reference(id));
    }

    /// <summary>
    /// Appends operators after the existing content, wrapping what is already there in a save and
    /// restore pair so that leftover graphics state cannot leak into the addition.
    /// </summary>
    /// <param name="content">The operators to append.</param>
    /// <param name="compress">Whether to deflate the stream.</param>
    public void AppendContent(ReadOnlyMemory<byte> content, bool compress = true)
    {
        var parts = new PdfArray { Resolver = _document };

        PdfValue existing = Dictionary[PdfNames.Contents];
        if (!existing.IsNull)
        {
            PdfObjectId saveId = _document.AddObject(PdfValue.Stream(PdfStream.FromData("q\n"u8.ToArray(), compress)));
            parts.Add(PdfValue.Reference(saveId));

            if (Dictionary.Get(PdfNames.Contents).AsArray is { } array)
            {
                for (int i = 0; i < array.Count; i++)
                    parts.Add(array[i]);
            }
            else
            {
                parts.Add(existing);
            }

            PdfObjectId restoreId = _document.AddObject(PdfValue.Stream(PdfStream.FromData("\nQ\n"u8.ToArray(), compress)));
            parts.Add(PdfValue.Reference(restoreId));
        }

        PdfObjectId addedId = _document.AddObject(PdfValue.Stream(PdfStream.FromData(content, compress)));
        parts.Add(PdfValue.Reference(addedId));

        Set(PdfNames.Contents, PdfValue.Array(parts));
    }

    /// <summary>Extracts the text of the page, in the order the content stream painted it.</summary>
    /// <param name="options">What to do beyond reading the glyphs; the defaults do nothing extra.</param>
    public string ExtractText(Text.TextExtractionOptions? options = null) =>
        Text.TextExtractor.ExtractText(this, options);

    /// <summary>Extracts every glyph on the page with its position and size.</summary>
    /// <param name="options">What to do beyond reading the glyphs; the defaults do nothing extra.</param>
    public IReadOnlyList<Text.PdfLetter> ExtractLetters(Text.TextExtractionOptions? options = null) =>
        Text.TextExtractor.ExtractLetters(this, options);

    /// <summary>
    /// The images the page draws, including those inside the forms it draws. Each is found once
    /// however many times the content stream paints it.
    /// </summary>
    public IReadOnlyList<Images.PdfPageImage> Images => Inkwright.Images.PageImageFinder.Find(this);

    /// <summary>Finds a phrase on the page and reports the areas it covers.</summary>
    /// <param name="phrase">The text to look for.</param>
    /// <param name="options">How closely to match; the defaults look past accents and ligatures.</param>
    public IReadOnlyList<Text.PdfTextMatch> FindText(string phrase, Text.TextSearchOptions? options = null) =>
        Text.TextSearch.Find(ExtractLetters(), phrase, options);

    /// <summary>Finds a phrase on the page, comparing the printed characters as they are.</summary>
    /// <param name="phrase">The text to look for.</param>
    /// <param name="comparison">How to compare.</param>
    public IReadOnlyList<Text.PdfTextMatch> FindText(string phrase, StringComparison comparison) =>
        Text.TextSearch.Find(ExtractLetters(), phrase, comparison);

    /// <summary>
    /// The transformation from user space to a space with the origin at the top-left corner and the
    /// y axis pointing down, which is how layout code prefers to think.
    /// </summary>
    public PdfMatrix TopLeftTransform()
    {
        PdfRectangle box = CropBox;
        PdfMatrix flip = new(1, 0, 0, -1, -box.Left, box.Top);

        return Rotation switch
        {
            90 => flip * new PdfMatrix(0, 1, -1, 0, box.Height, 0),
            180 => flip * new PdfMatrix(-1, 0, 0, -1, box.Width, box.Height),
            270 => flip * new PdfMatrix(0, -1, 1, 0, 0, box.Width),
            _ => flip,
        };
    }

    /// <summary>
    /// Adds an output intent to this page (ISO 32000-2 §14.11.5): the colour characterisation of
    /// the device this particular page was prepared for. It sits on the page rather than the
    /// document, which is how a file made of pages meant for different devices keeps them straight.
    /// </summary>
    /// <param name="intent">The intent to add, carrying its embedded ICC profile.</param>
    public void AddOutputIntent(PdfOutputIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        EnsureId();
        _document.Version = PdfVersion.Max(_document.Version, PdfVersion.Pdf20);

        PdfArray intents = Dictionary.GetArray(PdfName.Get("OutputIntents")) ?? Attach();
        intents.Add(intent.Build(_document));
        _document.MarkChanged(Id);

        PdfArray Attach()
        {
            var array = new PdfArray();
            _document.Attach(PdfValue.Array(array));
            Dictionary.Set(PdfName.Get("OutputIntents"), PdfValue.Array(array));
            return array;
        }
    }

    /// <summary>
    /// Makes sure the page has an object identity, giving it one now if the model has not yet
    /// assigned it. A destination that points at a page needs a stable reference before the file
    /// is laid out, and a page created in this session only gets its number at save time otherwise.
    /// </summary>
    internal PdfObjectId EnsureId()
    {
        if (!Id.IsValid)
            Id = _document.AddObject(PdfValue.Dictionary(Dictionary));

        return Id;
    }

    /// <inheritdoc />
    public override string ToString() => $"Page {Id.Number} ({Width:0.#} x {Height:0.#} pt)";
}
