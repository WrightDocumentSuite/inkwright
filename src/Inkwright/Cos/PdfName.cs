using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Inkwright.Cos;

/// <summary>
/// A PDF name object (ISO 32000-1 §7.3.5). Names compare by value, so instances obtained from
/// different sources are interchangeable; well-known names are interned once for the process and
/// the reader interns the rest per document.
/// </summary>
public sealed class PdfName : IEquatable<PdfName>, IComparable<PdfName>
{
    private static readonly FrozenDictionary<string, PdfName> WellKnown = BuildWellKnown();
    private static readonly FrozenDictionary<string, PdfName>.AlternateLookup<ReadOnlySpan<char>> WellKnownBySpan =
        WellKnown.GetAlternateLookup<ReadOnlySpan<char>>();

    private readonly int _hash;
    private byte[]? _utf8;

    private PdfName(string value, byte[]? utf8)
    {
        Value = value;
        _utf8 = utf8;
        _hash = string.GetHashCode(value, StringComparison.Ordinal);
    }

    /// <summary>The decoded name, without the leading solidus and with <c>#xx</c> escapes resolved.</summary>
    public string Value { get; }

    /// <summary>The UTF-8 bytes of <see cref="Value"/>, as they appear in the file before escaping.</summary>
    internal ReadOnlySpan<byte> Utf8 => _utf8 ??= Encoding.UTF8.GetBytes(Value);

    /// <summary>Returns the interned name for <paramref name="value"/>, allocating only for unknown names.</summary>
    /// <param name="value">The decoded name without its leading solidus.</param>
    public static PdfName Get(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return WellKnown.TryGetValue(value, out PdfName? known) ? known : new PdfName(value, null);
    }

    /// <summary>Returns the interned name for a raw byte sequence read from a file.</summary>
    /// <param name="utf8">The name bytes with <c>#xx</c> escapes already resolved.</param>
    public static PdfName Get(ReadOnlySpan<byte> utf8)
    {
        if (utf8.Length <= 64 && Ascii.IsValid(utf8))
        {
            Span<char> chars = stackalloc char[utf8.Length];
            for (int i = 0; i < utf8.Length; i++)
                chars[i] = (char)utf8[i];

            if (WellKnownBySpan.TryGetValue(chars, out PdfName? known))
                return known;

            return new PdfName(new string(chars), utf8.ToArray());
        }

        string decoded = Encoding.UTF8.GetString(utf8);
        return WellKnown.TryGetValue(decoded, out PdfName? knownWide)
            ? knownWide
            : new PdfName(decoded, utf8.ToArray());
    }

    /// <inheritdoc />
    public bool Equals(PdfName? other) =>
        other is not null && (ReferenceEquals(this, other) ||
                              (_hash == other._hash && string.Equals(Value, other.Value, StringComparison.Ordinal)));

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as PdfName);

    /// <inheritdoc />
    public override int GetHashCode() => _hash;

    /// <inheritdoc />
    public int CompareTo(PdfName? other) =>
        other is null ? 1 : string.CompareOrdinal(Value, other.Value);

    /// <summary>Compares two names by value.</summary>
    public static bool operator ==(PdfName? left, PdfName? right) =>
        left is null ? right is null : left.Equals(right);

    /// <summary>Compares two names by value.</summary>
    public static bool operator !=(PdfName? left, PdfName? right) => !(left == right);

    /// <inheritdoc />
    public override string ToString() => "/" + Value;

    [SuppressMessage("Performance", "CA1861", Justification = "Table is built once into a frozen dictionary.")]
    private static FrozenDictionary<string, PdfName> BuildWellKnown()
    {
        string[] names =
        [
            // Structure and catalog
            "Type", "Subtype", "Length", "Length1", "Length2", "Length3", "Filter", "DecodeParms", "F", "FDecodeParms",
            "Catalog", "Pages", "Page", "Kids", "Count", "Parent", "Root", "Info", "Encrypt", "ID", "Size", "Prev",
            "XRefStm", "Index", "W", "ObjStm", "XRef", "N", "First", "Extends", "Version", "Extensions",
            "Linearized", "L", "O", "E", "T", "H", "P", "V", "R", "U", "OE", "UE", "Perms", "EncryptMetadata",
            "StmF", "StrF", "EFF", "CF", "StdCF", "Identity", "AESV2", "AESV3", "V2", "AuthEvent", "DocOpen", "CFM",

            // Page tree and page attributes
            "MediaBox", "CropBox", "BleedBox", "TrimBox", "ArtBox", "Resources", "Contents", "Rotate", "Group",
            "Annots", "UserUnit", "Tabs", "StructParents", "StructParent", "Metadata", "PieceInfo", "LastModified",
            "Thumb", "B", "Dur", "Trans", "AA", "PZ", "SeparationInfo", "TemplateInstantiated", "PresSteps",

            // Resources
            "Font", "XObject", "ExtGState", "ColorSpace", "Pattern", "Shading", "Properties", "ProcSet",

            // Fonts
            "BaseFont", "FirstChar", "LastChar", "Widths", "Encoding", "Differences", "BaseEncoding", "ToUnicode",
            "FontDescriptor", "Type0", "Type1", "Type1C", "MMType1", "Type3", "TrueType", "CIDFontType0",
            "CIDFontType0C", "CIDFontType2", "DescendantFonts", "CIDSystemInfo", "Registry", "Ordering",
            "Supplement", "CIDToGIDMap", "DW", "DW2", "CIDSet", "FontFile", "FontFile2", "FontFile3", "FontBBox",
            "FontMatrix", "CharProcs", "MissingWidth", "Flags", "ItalicAngle", "Ascent", "Descent", "CapHeight",
            "XHeight", "StemV", "StemH", "AvgWidth", "MaxWidth", "Leading", "FontName", "FontFamily", "FontStretch",
            "FontWeight", "WinAnsiEncoding", "MacRomanEncoding", "MacExpertEncoding", "StandardEncoding",
            "PDFDocEncoding", "Identity-H", "Identity-V", "OpenType",

            // Graphics and images
            "Image", "Form", "Width", "Height", "BitsPerComponent", "ColorSpace", "Decode", "DecodeParms",
            "ImageMask", "Mask", "SMask", "SMaskInData", "Interpolate", "Alternates", "Intent", "OC", "BBox",
            "Matrix", "FormType", "DeviceGray", "DeviceRGB", "DeviceCMYK", "DeviceN", "Indexed", "ICCBased",
            "CalRGB", "CalGray", "Lab", "Separation", "PatternType", "ShadingType", "Function", "FunctionType",
            "Domain", "Range", "C0", "C1", "Bounds", "Encode", "Functions", "Coords", "Extend", "Background",
            "AntiAlias", "PaintType", "TilingType", "XStep", "YStep", "Predictor", "Colors", "Columns", "EarlyChange",
            "BlackIs1", "K", "EndOfLine", "EncodedByteAlign", "EndOfBlock", "DamagedRowsBeforeError", "Rows",
            "JBIG2Globals", "ColorTransform", "Quality",

            // Filters
            "FlateDecode", "LZWDecode", "ASCII85Decode", "ASCIIHexDecode", "RunLengthDecode", "CCITTFaxDecode",
            "JBIG2Decode", "DCTDecode", "JPXDecode", "Crypt", "AHx", "A85", "LZW", "Fl", "RL", "CCF", "DCT",

            // Graphics state
            "LW", "LC", "LJ", "ML", "D", "RI", "OP", "op", "OPM", "SA", "BM", "SMask", "CA", "ca", "AIS", "TK",
            "Normal", "Compatible", "Multiply", "Screen", "Overlay", "Darken", "Lighten", "ColorDodge", "ColorBurn",
            "HardLight", "SoftLight", "Difference", "Exclusion", "Hue", "Saturation", "Color", "Luminosity",
            "Transparency", "CS", "S", "I", "G", "Luminance", "Alpha", "TR", "TR2", "HT", "FL", "BG", "BG2", "UCR",
            "UCR2", "PCSp", "GS",

            // Annotations
            "Annot", "Rect", "Border", "BS", "Link", "Widget", "Text", "FreeText", "Square", "Circle", "Line",
            "Polygon", "PolyLine", "Highlight", "Underline", "Squiggly", "StrikeOut", "Stamp", "Caret", "Ink",
            "Popup", "FileAttachment", "Sound", "Movie", "Screen", "PrinterMark", "TrapNet", "Watermark", "3D",
            "Redact", "A", "Dest", "M", "NM", "AP", "AS", "IC", "QuadPoints", "InkList", "Vertices", "L", "LE",
            "RD", "CL", "IT", "BE", "RC", "CreationDate", "IRT", "RT", "Open", "Name", "State", "StateModel",
            "Off", "Yes", "MK", "BC", "BG", "Rotate", "Sig", "SigFlags", "Lock", "SV",

            // Actions and destinations
            "Action", "GoTo", "GoToR", "GoToE", "Launch", "Thread", "URI", "Named", "SubmitForm", "ResetForm",
            "ImportData", "JavaScript", "Rendition", "SetOCGState", "Hide", "Next", "XYZ", "Fit", "FitH", "FitV",
            "FitR", "FitB", "FitBH", "FitBV", "Dests", "Names", "Limits", "EmbeddedFiles", "JS", "Fields",

            // Interactive forms
            "AcroForm", "DA", "DR", "Q", "FT", "Ff", "V", "DV", "TU", "TM", "MaxLen", "Opt", "TI", "Btn", "Tx",
            "Ch", "NeedAppearances", "CO", "XFA", "RV", "Sig", "ByteRange", "SubFilter", "Contents", "Reason",
            "Location", "ContactInfo", "adbe.pkcs7.detached", "ETSI.CAdES.detached", "DocMDP", "FieldMDP",
            "TransformMethod", "TransformParams", "DigestMethod",

            // Outlines and structure
            "Outlines", "Title", "Last", "Next", "Prev", "First", "SE", "StructTreeRoot", "StructElem", "K",
            "ParentTree", "ParentTreeNextKey", "RoleMap", "ClassMap", "Pg", "Obj", "MCID", "MCR", "OBJR", "Alt",
            "ActualText", "Lang", "E", "T", "Document", "Part", "Art", "Sect", "Div", "P", "H1", "H2", "H3",
            "H4", "H5", "H6", "Lbl", "LI", "LBody", "Table", "TR", "TH", "TD", "THead", "TBody", "TFoot",
            "Caption", "Figure", "Formula", "Link", "Span", "Quote", "Note", "Reference", "BibEntry", "Code",
            "Artifact", "MarkInfo", "Marked", "Suspects", "UserProperties", "Scope", "RowSpan", "ColSpan",
            "Headers", "ID", "Nums", "NumberFormat",

            // Document information and viewer preferences
            "Author", "Subject", "Keywords", "Creator", "Producer", "ModDate", "Trapped", "ViewerPreferences",
            "PageLayout", "PageMode", "PageLabels", "OpenAction", "URI", "Threads", "OCProperties", "OCGs",
            "OCMDs", "Requirements", "Collection", "NeedsRendering", "AF", "DPart", "DSS", "Legal",
            "SinglePage", "OneColumn", "TwoColumnLeft", "TwoColumnRight", "TwoPageLeft", "TwoPageRight",
            "UseNone", "UseOutlines", "UseThumbs", "FullScreen", "UseOC", "UseAttachments", "Direction",
            "HideToolbar", "HideMenubar", "HideWindowUI", "FitWindow", "CenterWindow", "DisplayDocTitle",
            "NonFullScreenPageMode", "PrintScaling", "Duplex", "PickTrayByPDFSize", "PrintPageRange", "NumCopies",

            // File specifications, embedded files and output intents
            "Filespec", "FS", "UF", "DOS", "Mac", "Unix", "EF", "Desc", "Params", "Size", "CheckSum",
            "OutputIntent", "OutputIntents", "OutputConditionIdentifier", "OutputCondition", "RegistryName",
            "DestOutputProfile", "GTS_PDFA1", "GTS_PDFX", "Info", "S", "URL", "AFRelationship", "Data",
            "Alternative", "Source", "Supplement", "Unspecified", "EmbeddedFile",

            // Optional content
            "OCG", "OCMD", "OCGs", "VE", "BaseState", "ON", "OFF", "AS", "Category", "Order", "RBGroups",
            "ListMode", "Print", "View", "Design", "Zoom", "Export", "PrintState", "ViewState",
        ];

        var map = new Dictionary<string, PdfName>(names.Length, StringComparer.Ordinal);
        foreach (string name in names)
            map.TryAdd(name, new PdfName(name, Encoding.ASCII.GetBytes(name)));

        return map.ToFrozenDictionary(StringComparer.Ordinal);
    }
}
