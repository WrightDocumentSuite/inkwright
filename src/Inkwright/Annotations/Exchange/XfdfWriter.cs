using System.Xml.Linq;
using Inkwright.Cos;

namespace Inkwright.Annotations;

/// <summary>
/// Writes a document's comments and form values as XFDF (ISO 19444-1). Threads are written flat, in
/// the order a reader shows them, with each reply naming the comment it answers: XML has no object
/// numbers, so a name is the only way one annotation can point at another.
/// </summary>
internal static class XfdfWriter
{
    /// <summary>Writes a payload.</summary>
    /// <param name="document">The document to export.</param>
    /// <param name="href">The file the payload names as its source, or <see langword="null"/> for none.</param>
    /// <param name="content">Which halves of the payload to write.</param>
    internal static XDocument Write(PdfDocument document, string? href, PdfExchangeContent content)
    {
        var root = new XElement(
            Xfdf.Namespace + "xfdf",
            new XAttribute(XNamespace.Xml + "space", "preserve"),
            Source(href),
            Identity(document));

        // The order follows the content model of §6.2.2.1: the file, its identity, the fields and
        // then the annotations.
        if (content.HasFlag(PdfExchangeContent.Fields))
            root.Add(XfdfFields.Write(document));

        if (content.HasFlag(PdfExchangeContent.Annotations))
            root.Add(Annotations(document));

        return new XDocument(new XDeclaration("1.0", "UTF-8", null), root);
    }

    private static XElement Annotations(PdfDocument document)
    {
        var annots = new XElement(Xfdf.Namespace + "annots");

        for (int i = 0; i < document.Pages.Count; i++)
        {
            foreach (PdfMarkupAnnotation comment in document.Pages[i].Annotations.Comments)
            {
                foreach (PdfMarkupAnnotation member in comment.Thread)
                {
                    if (Element(member, i) is { } element)
                        annots.Add(element);
                }
            }
        }

        return annots;
    }

    /// <summary>
    /// The file the payload belongs to (§6.2.3). A round of review that names the document it came
    /// off can be checked against the one it is being imported into.
    /// </summary>
    private static XElement? Source(string? href) =>
        string.IsNullOrEmpty(href)
            ? null
            : new XElement(Xfdf.Namespace + "f", new XAttribute("href", href));

    /// <summary>
    /// The identity of the file the comments came off, so the far end can tell whether it is
    /// looking at the same document.
    /// </summary>
    private static XElement? Identity(PdfDocument document)
    {
        if (document.Trailer.GetArray(PdfNames.ID) is not { Count: > 0 } identity)
            return null;

        string original = Convert.ToHexString(identity.Get(0).AsStringBytes);
        string modified = Convert.ToHexString(identity.Get(identity.Count - 1).AsStringBytes);

        return new XElement(
            Xfdf.Namespace + "ids",
            new XAttribute("original", original),
            new XAttribute("modified", modified));
    }

    private static XElement? Element(PdfMarkupAnnotation annotation, int pageIndex)
    {
        if (Xfdf.ElementOf(annotation.Subtype) is not { } name)
            return null;

        var element = new XElement(Xfdf.Namespace + name);
        element.SetAttributeValue("page", pageIndex);
        element.SetAttributeValue("rect", Xfdf.FormatNumbers(
        [
            annotation.Rectangle.Left, annotation.Rectangle.Bottom,
            annotation.Rectangle.Right, annotation.Rectangle.Top,
        ]));

        WriteCommonAttributes(element, annotation);
        WriteShapeAttributes(element, annotation);
        WriteBorderStyle(element, annotation);
        WriteThreadAttributes(element, annotation);
        WriteChildren(element, annotation);

        return element;
    }

    private static void WriteCommonAttributes(XElement element, PdfMarkupAnnotation annotation)
    {
        PdfDictionary dictionary = annotation.Dictionary;

        element.SetAttributeValue("name", EnsureName(annotation));
        element.SetAttributeValue("title", Escape(annotation.Author));
        element.SetAttributeValue("subject", Escape(annotation.Subject));
        element.SetAttributeValue("date", dictionary.GetTextString(AnnotationNames.M));
        element.SetAttributeValue("creationdate", dictionary.GetTextString(AnnotationNames.CreationDate));
        element.SetAttributeValue("state", dictionary.GetTextString(AnnotationNames.State));
        element.SetAttributeValue("statemodel", dictionary.GetTextString(AnnotationNames.StateModel));
        element.SetAttributeValue("flags", Xfdf.FormatFlags(annotation.Flags));

        if (annotation.Color is { } color)
            element.SetAttributeValue("color", Xfdf.FormatColor(color));

        if (annotation is PdfShapeAnnotation { InteriorColor: { } interior })
            element.SetAttributeValue("interior-color", Xfdf.FormatColor(interior));

        if (annotation.Opacity < 1)
            element.SetAttributeValue("opacity", Xfdf.FormatNumbers([annotation.Opacity]));

        if (dictionary.GetName(AnnotationNames.Name)?.Value is { } icon)
            element.SetAttributeValue("icon", icon);

        if (dictionary.GetName(AnnotationNames.IT)?.Value is { } intent)
            element.SetAttributeValue("intent", intent);

        if (dictionary.ContainsKey(PdfName.Get("Rotate")))
            element.SetAttributeValue("rotation", dictionary.GetInteger(PdfName.Get("Rotate")));
    }

    private static void WriteShapeAttributes(XElement element, PdfMarkupAnnotation annotation)
    {
        PdfDictionary dictionary = annotation.Dictionary;

        WriteNumbers(element, "coords", dictionary.GetArray(AnnotationNames.QuadPoints));
        WriteNumbers(element, "fringe", dictionary.GetArray(PdfName.Get("RD")));

        if (annotation is PdfLineAnnotation line)
        {
            element.SetAttributeValue("start", Xfdf.FormatNumbers([line.Start.X, line.Start.Y]));
            element.SetAttributeValue("end", Xfdf.FormatNumbers([line.End.X, line.End.Y]));
            element.SetAttributeValue("head", LineEndings.Name(line.StartEnding));
            element.SetAttributeValue("tail", LineEndings.Name(line.EndEnding));
        }

        if (annotation is not PdfFreeTextAnnotation text)
            return;

        element.SetAttributeValue("justification", Justification(text.Alignment));
        WriteNumbers(element, "callout", annotation.Dictionary.GetArray(PdfName.Get("CL")));
    }

    private static void WriteBorderStyle(XElement element, PdfMarkupAnnotation annotation)
    {
        if (annotation.Dictionary.GetDictionary(AnnotationNames.BS) is not { } border)
            return;

        if (border.ContainsKey(AnnotationNames.W))
            element.SetAttributeValue("width", Xfdf.FormatNumbers([border.GetNumber(AnnotationNames.W, 1)]));

        WriteNumbers(element, "dashes", border.GetArray(PdfName.Get("D")));
        element.SetAttributeValue("style", BorderStyle(border.GetName(AnnotationNames.S)?.Value));
    }

    private static void WriteThreadAttributes(XElement element, PdfMarkupAnnotation annotation)
    {
        if (annotation.InReplyTo is not { } parent)
            return;

        element.SetAttributeValue("inreplyto", EnsureName(parent));
        element.SetAttributeValue(
            "replyType",
            annotation.Dictionary.GetName(AnnotationNames.RT) == AnnotationNames.Group ? "group" : "reply");
    }

    private static void WriteChildren(XElement element, PdfMarkupAnnotation annotation)
    {
        PdfDictionary dictionary = annotation.Dictionary;

        if (annotation.Contents is { Length: > 0 } contents)
            element.Add(new XElement(Xfdf.Namespace + "contents", Xfdf.Escape(contents)));

        if (dictionary.GetTextString(AnnotationNames.RC) is { Length: > 0 } rich)
            element.Add(new XElement(Xfdf.Namespace + "contents-richtext", rich));

        if (dictionary.GetTextString(AnnotationNames.DA) is { Length: > 0 } appearance)
            element.Add(new XElement(Xfdf.Namespace + "defaultappearance", appearance));

        if (dictionary.GetTextString(PdfName.Get("DS")) is { Length: > 0 } style)
            element.Add(new XElement(Xfdf.Namespace + "defaultstyle", Xfdf.Escape(style)));

        WriteVertices(element, dictionary);
        WriteInk(element, annotation);
        WriteAttachment(element, annotation);
        WritePopup(element, annotation);
    }

    private static void WriteVertices(XElement element, PdfDictionary dictionary)
    {
        if (dictionary.GetArray(AnnotationNames.Vertices) is { Count: > 0 } vertices)
            element.Add(new XElement(Xfdf.Namespace + "vertices", Xfdf.FormatNumbers(Numbers(vertices))));
    }

    private static void WriteInk(XElement element, PdfMarkupAnnotation annotation)
    {
        if (annotation is not PdfInkAnnotation ink || ink.Strokes.Count == 0)
            return;

        var list = new XElement(Xfdf.Namespace + "inklist");

        foreach (IReadOnlyList<PdfPoint> stroke in ink.Strokes)
        {
            List<double> points = [];
            foreach (PdfPoint point in stroke)
            {
                points.Add(point.X);
                points.Add(point.Y);
            }

            list.Add(new XElement(Xfdf.Namespace + "gesture", Xfdf.FormatNumbers(points)));
        }

        element.Add(list);
    }

    /// <summary>
    /// Sends the attached file along with the pin. XFDF has no place for a stream, so the bytes go
    /// base64-encoded; a pin that arrives without its file opens onto nothing.
    /// </summary>
    private static void WriteAttachment(XElement element, PdfMarkupAnnotation annotation)
    {
        if (annotation is not PdfFileAttachmentAnnotation attachment ||
            annotation.Dictionary.GetDictionary(AnnotationNames.FS) is not { } specification)
        {
            return;
        }

        element.SetAttributeValue("file", attachment.FileName ?? "attachment");

        if (specification.GetDictionary(PdfNames.EF)?.GetStream(PdfNames.F) is not { } file)
            return;

        if (file.Dictionary.GetName(PdfNames.Subtype)?.Value is { } mime)
            element.SetAttributeValue("mimetype", mime);

        string encoded;
        try
        {
            encoded = Convert.ToBase64String(file.DecodedSpan());
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            // A file whose bytes cannot be decoded is left behind rather than sent as rubbish; the
            // pin itself still travels, so the far end knows something was attached here.
            return;
        }

        element.Add(new XElement(
            Xfdf.Namespace + "data",
            new XAttribute("MODE", "raw"),
            new XAttribute("encoding", "base64"),
            encoded));
    }

    private static void WritePopup(XElement element, PdfMarkupAnnotation annotation)
    {
        if (annotation.PopupAnnotation is not { } popup)
            return;

        PdfRectangle bounds = PdfRectangle.FromArray(popup.GetArray(AnnotationNames.Rect));

        element.Add(new XElement(
            Xfdf.Namespace + "popup",
            new XAttribute("page", element.Attribute("page")!.Value),
            new XAttribute("rect", Xfdf.FormatNumbers([bounds.Left, bounds.Bottom, bounds.Right, bounds.Top])),
            new XAttribute("open", popup.GetBoolean(AnnotationNames.Open) ? "yes" : "no")));
    }

    /// <summary>
    /// The name the far end will know this comment by. A comment that has none is given one now:
    /// without it the next round of review cannot tell an answer from a second copy.
    /// </summary>
    private static string EnsureName(PdfAnnotation annotation)
    {
        if (annotation.Name is { Length: > 0 } name)
            return name;

        string minted = Guid.NewGuid().ToString("N");
        annotation.Name = minted;
        annotation.Document.MarkChanged(annotation.Id);
        return minted;
    }

    private static void WriteNumbers(XElement element, string name, PdfArray? array)
    {
        if (array is { Count: > 0 })
            element.SetAttributeValue(name, Xfdf.FormatNumbers(Numbers(array)));
    }

    private static List<double> Numbers(PdfArray array)
    {
        List<double> numbers = new(array.Count);
        for (int i = 0; i < array.Count; i++)
            numbers.Add(array.GetNumber(i));

        return numbers;
    }

    private static string? Escape(string? text) => text is null ? null : Xfdf.Escape(text);

    private static string Justification(int alignment) => alignment switch
    {
        1 => "centered",
        2 => "right",
        _ => "left",
    };

    private static string BorderStyle(string? style) => style switch
    {
        "D" => "dash",
        "B" => "bevelled",
        "I" => "inset",
        "U" => "underline",
        _ => "solid",
    };
}
