using System.Xml;
using System.Xml.Linq;
using Inkwright.Cos;
using Inkwright.Diagnostics;

namespace Inkwright.Annotations;

/// <summary>
/// Turns an XFDF round of review into annotation dictionaries. Elements are matched on their local
/// name alone, because plenty of producers write the vocabulary of ISO 19444-1 without ever
/// declaring its namespace, and refusing those files would help nobody.
/// </summary>
internal static class XfdfReader
{
    /// <summary>What one payload holds: comments to place and field values to write.</summary>
    /// <param name="Annotations">The comments the payload carries.</param>
    /// <param name="Fields">The field values the payload carries.</param>
    internal readonly record struct Payload(
        List<IncomingAnnotation> Annotations, List<IncomingField> Fields);

    internal static Payload Read(PdfDocument document, string xfdf, List<string> warnings)
    {
        XDocument parsed;

        try
        {
            parsed = XDocument.Parse(xfdf, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException exception)
        {
            throw new PdfFormatException($"The XFDF is not well-formed XML: {exception.Message}", exception);
        }

        if (parsed.Root is not { } root || root.Name.LocalName != "xfdf")
            throw new PdfFormatException("The file is XML, but its root element is not <xfdf>.");

        CheckIdentity(document, root, warnings);

        List<IncomingField> fields = XfdfFields.Read(root);
        List<IncomingAnnotation> incoming = [];

        if (Child(root, "annots") is not { } annots)
        {
            // A payload of form data alone is perfectly ordinary: that is what a submitted form is.
            if (fields.Count == 0)
                warnings.Add("The XFDF carries neither <fields> nor <annots>, so there is nothing to import.");

            return new Payload(incoming, fields);
        }

        foreach (XElement element in annots.Elements())
        {
            if (Xfdf.SubtypeOf(element.Name.LocalName) is not { } subtype)
            {
                warnings.Add($"The XFDF holds a <{element.Name.LocalName}> element, which is not a comment.");
                continue;
            }

            incoming.Add(ReadAnnotation(document, element, subtype, warnings));
        }

        return new Payload(incoming, fields);
    }

    /// <summary>
    /// Warns when the payload was written against a different file. It is only a warning: the same
    /// review often travels between a draft and the copy somebody actually kept, and the comments
    /// are still worth having.
    /// </summary>
    private static void CheckIdentity(PdfDocument document, XElement root, List<string> warnings)
    {
        if (Child(root, "ids")?.Attribute("original")?.Value is not { Length: > 0 } original)
            return;

        if (document.Trailer.GetArray(PdfNames.ID) is not { Count: > 0 } identity)
            return;

        string mine = Convert.ToHexString(identity.Get(0).AsStringBytes);
        if (!string.Equals(mine, original, StringComparison.OrdinalIgnoreCase))
            warnings.Add("The XFDF was written against a different file; the comments were imported anyway.");
    }

    private static IncomingAnnotation ReadAnnotation(
        PdfDocument document, XElement element, string subtype, List<string> warnings)
    {
        var dictionary = new PdfDictionary(16);
        dictionary.Set(AnnotationNames.Type, PdfValue.Name(AnnotationNames.Annot));
        dictionary.Set(AnnotationNames.Subtype, PdfValue.Name(PdfName.Get(subtype)));

        ReadCommonAttributes(element, dictionary);
        ReadShapeAttributes(element, dictionary);
        ReadBorderStyle(element, dictionary);
        ReadChildren(document, element, dictionary, subtype, warnings);

        XElement? popup = Child(element, "popup");
        string? replyType = Attribute(element, "replyType");

        return new IncomingAnnotation
        {
            PageIndex = Integer(element, "page") ?? 0,
            Dictionary = dictionary,
            InReplyTo = Attribute(element, "inreplyto"),
            IsGroupedWithParent = replyType is not null &&
                                  replyType.Contains("group", StringComparison.OrdinalIgnoreCase),
            WantsPopup = popup is not null,
            PopupBounds = popup is null ? null : Rectangle(popup, "rect"),
            PopupOpen = string.Equals(Attribute(popup, "open"), "yes", StringComparison.OrdinalIgnoreCase),
        };
    }

    /// <summary>The attributes every annotation may carry, whatever it draws (tables 6 to 8).</summary>
    private static void ReadCommonAttributes(XElement element, PdfDictionary dictionary)
    {
        if (Rectangle(element, "rect") is { } bounds)
            dictionary.Set(AnnotationNames.Rect, PdfValue.Array(bounds.ToArray()));

        SetText(dictionary, AnnotationNames.NM, Attribute(element, "name"));
        SetText(dictionary, AnnotationNames.T, Unescape(Attribute(element, "title")));
        SetText(dictionary, AnnotationNames.Subj, Unescape(Attribute(element, "subject")));
        SetText(dictionary, AnnotationNames.M, Attribute(element, "date"));
        SetText(dictionary, AnnotationNames.CreationDate, Attribute(element, "creationdate"));
        SetText(dictionary, AnnotationNames.State, Attribute(element, "state"));
        SetText(dictionary, AnnotationNames.StateModel, Attribute(element, "statemodel"));

        SetColor(dictionary, AnnotationNames.C, Attribute(element, "color"));
        SetColor(dictionary, AnnotationNames.IC, Attribute(element, "interior-color"));

        if (Attribute(element, "flags") is { } flags)
            dictionary.Set(AnnotationNames.F, PdfValue.Integer((long)Xfdf.ParseFlags(flags)));

        if (Number(element, "opacity") is { } opacity)
            dictionary.Set(AnnotationNames.CA, PdfValue.Number(opacity));

        if (Integer(element, "rotation") is { } rotation)
            dictionary.Set(PdfName.Get("Rotate"), PdfValue.Integer(rotation));

        if (Attribute(element, "icon") is { Length: > 0 } icon)
            dictionary.Set(AnnotationNames.Name, PdfValue.Name(PdfName.Get(icon)));

        if (Attribute(element, "intent") is { Length: > 0 } intent)
            dictionary.Set(AnnotationNames.IT, PdfValue.Name(PdfName.Get(intent)));
    }

    /// <summary>The attributes that say where a mark, a line or a box actually lies.</summary>
    private static void ReadShapeAttributes(XElement element, PdfDictionary dictionary)
    {
        if (Attribute(element, "coords") is { } coords)
            SetNumbers(dictionary, AnnotationNames.QuadPoints, Xfdf.ParseNumbers(coords));

        if (Attribute(element, "fringe") is { } fringe)
            SetNumbers(dictionary, PdfName.Get("RD"), Xfdf.ParseNumbers(fringe));

        if (Attribute(element, "start") is { } start && Attribute(element, "end") is { } end)
        {
            List<double> line = [.. Xfdf.ParseNumbers(start), .. Xfdf.ParseNumbers(end)];
            SetNumbers(dictionary, AnnotationNames.L, line);
        }

        string? head = Attribute(element, "head");
        string? tail = Attribute(element, "tail");

        if (head is not null || tail is not null)
        {
            var endings = new PdfArray(2);
            endings.Add(PdfValue.Name(PdfName.Get(head ?? "None")));
            endings.Add(PdfValue.Name(PdfName.Get(tail ?? "None")));
            dictionary.Set(AnnotationNames.LE, PdfValue.Array(endings));
        }

        if (Attribute(element, "justification") is { } justification)
            dictionary.Set(AnnotationNames.Q, PdfValue.Integer(Alignment(justification)));

        if (Attribute(element, "callout") is { } callout)
            SetNumbers(dictionary, PdfName.Get("CL"), Xfdf.ParseNumbers(callout));
    }

    /// <summary>The border width, dash pattern and style, which live in one dictionary of their own.</summary>
    private static void ReadBorderStyle(XElement element, PdfDictionary dictionary)
    {
        double? width = Number(element, "width");
        string? dashes = Attribute(element, "dashes");
        string? style = Attribute(element, "style");

        if (width is null && dashes is null && style is null)
            return;

        var border = new PdfDictionary(4);
        border.Set(AnnotationNames.Type, PdfValue.Name(PdfName.Get("Border")));
        border.Set(AnnotationNames.W, PdfValue.Number(width ?? 1));
        border.Set(AnnotationNames.S, PdfValue.Name(PdfName.Get(BorderStyle(style))));

        if (dashes is { } pattern)
            SetNumbers(border, PdfName.Get("D"), Xfdf.ParseNumbers(pattern));

        dictionary.Set(AnnotationNames.BS, PdfValue.Dictionary(border));
    }

    private static void ReadChildren(
        PdfDocument document, XElement element, PdfDictionary dictionary, string subtype, List<string> warnings)
    {
        if (Child(element, "contents") is { } contents)
            SetText(dictionary, AnnotationNames.Contents, AnnotationText.ToPdf(Xfdf.Unescape(contents.Value)));

        // Rich text is markup inside markup: a producer that nests real elements is read back as
        // the XML it wrote, one that escaped the whole thing as text is read back unescaped.
        if (Child(element, "contents-richtext") is { } rich)
            SetText(dictionary, AnnotationNames.RC, rich.HasElements ? string.Concat(rich.Nodes()) : rich.Value);

        if (Child(element, "defaultappearance") is { } appearance)
            SetText(dictionary, AnnotationNames.DA, appearance.Value);

        if (Child(element, "defaultstyle") is { } style)
            SetText(dictionary, PdfName.Get("DS"), style.Value);

        if (Child(element, "vertices") is { } vertices)
            SetNumbers(dictionary, AnnotationNames.Vertices, Xfdf.ParseNumbers(vertices.Value));

        if (Child(element, "inklist") is { } ink)
            ReadInk(dictionary, ink);

        if (subtype == "FileAttachment")
            ReadAttachment(document, element, dictionary, warnings);

        if (Child(element, "appearance") is not null)
        {
            warnings.Add(
                "A comment brought a drawing of its own, which this import cannot read; " +
                "it was drawn from its type and colour instead.");
        }
    }

    private static void ReadInk(PdfDictionary dictionary, XElement inklist)
    {
        var strokes = new PdfArray();

        foreach (XElement gesture in inklist.Elements())
        {
            if (gesture.Name.LocalName != "gesture")
                continue;

            List<double> points = Xfdf.ParseNumbers(gesture.Value);
            if (points.Count >= 2)
                strokes.Add(PdfValue.Array(PdfArray.OfNumbers(points.ToArray())));
        }

        if (strokes.Count > 0)
            dictionary.Set(AnnotationNames.InkList, PdfValue.Array(strokes));
    }

    /// <summary>
    /// Rebuilds the file behind a pin. The bytes travel base64-encoded in a <c>data</c> element, and
    /// without them the pin would open onto nothing.
    /// </summary>
    private static void ReadAttachment(
        PdfDocument document, XElement element, PdfDictionary dictionary, List<string> warnings)
    {
        string name = Attribute(element, "file") ?? "attachment";

        if (Child(element, "data") is not { } data)
        {
            warnings.Add($"The attachment '{name}' arrived without its contents, so the pin opens onto nothing.");
            return;
        }

        try
        {
            byte[] content = Convert.FromBase64String(data.Value.Trim());
            dictionary.Set(AnnotationNames.FS, document.Attachments.Add(
                name, content, Attribute(element, "mimetype") ?? "application/octet-stream"));
        }
        catch (FormatException)
        {
            warnings.Add($"The contents of the attachment '{name}' are not valid base64 and were left out.");
        }
    }

    private static int Alignment(string justification) => justification.ToLowerInvariant() switch
    {
        "centered" or "1" => 1,
        "right" or "2" => 2,
        _ => 0,
    };

    private static string BorderStyle(string? style) => style?.ToLowerInvariant() switch
    {
        "dash" => "D",
        "bevelled" => "B",
        "inset" => "I",
        "underline" => "U",
        "cloudy" => "S",
        _ => "S",
    };

    private static XElement? Child(XElement? parent, string name)
    {
        foreach (XElement child in parent?.Elements() ?? [])
        {
            if (child.Name.LocalName == name)
                return child;
        }

        return null;
    }

    private static string? Attribute(XElement? element, string name) =>
        element?.Attribute(name)?.Value;

    private static string? Unescape(string? text) => text is null ? null : Xfdf.Unescape(text);

    private static double? Number(XElement element, string name) =>
        Attribute(element, name) is { } text && Xfdf.ParseNumbers(text) is [double first, ..] ? first : null;

    private static int? Integer(XElement element, string name) =>
        Number(element, name) is { } value ? (int)Math.Round(value) : null;

    private static PdfRectangle? Rectangle(XElement? element, string name)
    {
        if (Attribute(element, name) is not { } text)
            return null;

        return Xfdf.ParseNumbers(text) is [double left, double bottom, double right, double top, ..]
            ? new PdfRectangle(left, bottom, right, top)
            : null;
    }

    private static void SetText(PdfDictionary dictionary, PdfName key, string? value)
    {
        if (!string.IsNullOrEmpty(value))
            dictionary.Set(key, PdfValue.TextString(value));
    }

    private static void SetColor(PdfDictionary dictionary, PdfName key, string? value)
    {
        if (value is null || Xfdf.ParseColor(value) is not { } color)
            return;

        dictionary.Set(key, PdfValue.Array(PdfArray.OfNumbers(
            color.Component0, color.Component1, color.Component2)));
    }

    private static void SetNumbers(PdfDictionary dictionary, PdfName key, List<double> numbers)
    {
        if (numbers.Count > 0)
            dictionary.Set(key, PdfValue.Array(PdfArray.OfNumbers(numbers.ToArray())));
    }
}
