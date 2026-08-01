using System.Globalization;
using System.Text;
using Inkwright.Content;
using Inkwright.Cos;
using Inkwright.Fonts;

namespace Inkwright.Forms;

/// <summary>
/// Draws what a field looks like. Viewers are supposed to build appearances from a field's default
/// appearance string, and many do not, so the library builds them itself: a filled form should look
/// filled everywhere, not only in the one reader that bothers.
/// </summary>
internal sealed class AppearanceGenerator(PdfForm form)
{
    private readonly PdfForm _form = form;

    internal void Update(PdfFormField field)
    {
        switch (field.Type)
        {
            case PdfFieldType.Text:
            case PdfFieldType.ComboBox:
            case PdfFieldType.ListBox:
                foreach (PdfDictionary widget in field.Widgets)
                    BuildTextAppearance(field, widget);

                break;

            default:
                // Buttons already carry an appearance for each state; only the selected state changes.
                break;
        }
    }

    internal void FlattenPage(PdfPage page, IReadOnlyList<PdfFormField> fields)
    {
        if (page.Dictionary.GetArray(PdfName.Get("Annots")) is not { Count: > 0 } annotations)
            return;

        HashSet<PdfDictionary> widgets = [];
        foreach (PdfFormField field in fields)
        {
            foreach (PdfDictionary widget in field.Widgets)
                widgets.Add(widget);
        }

        var keep = new PdfArray();
        using ContentCanvas canvas = ContentCanvas.ForPage(page);
        bool drewAnything = false;

        for (int i = 0; i < annotations.Count; i++)
        {
            PdfValue entry = annotations[i];
            PdfDictionary? annotation = annotations.Get(i).AsDictionary;

            if (annotation is null ||
                annotation.GetName(PdfName.Get("Subtype"))?.Value != "Widget" ||
                !widgets.Contains(annotation))
            {
                keep.Add(entry);
                continue;
            }

            if (Stamp(canvas, annotation))
                drewAnything = true;
        }

        if (drewAnything)
            canvas.Commit();

        if (keep.Count == 0)
            page.Dictionary.Remove(PdfName.Get("Annots"));
        else
            page.Dictionary.Set(PdfName.Get("Annots"), PdfValue.Array(keep));

        page.MarkChanged();
    }

    private bool Stamp(ContentCanvas canvas, PdfDictionary annotation)
    {
        if (FindAppearance(annotation) is not { } appearance)
            return false;

        PdfRectangle rect = PdfRectangle.FromArray(annotation.GetArray(PdfName.Get("Rect")));
        if (rect.IsEmpty)
            return false;

        PdfRectangle box = PdfRectangle.FromArray(appearance.Dictionary.GetArray(PdfName.Get("BBox")));
        PdfMatrix matrix = PdfMatrix.FromArray(appearance.Dictionary.GetArray(PdfName.Get("Matrix")));

        // §12.5.5: the transformed bounding box is mapped onto the annotation rectangle.
        PdfRectangle transformed = box.IsEmpty ? rect : box.Transform(matrix);
        double scaleX = transformed.Width > 0 ? rect.Width / transformed.Width : 1;
        double scaleY = transformed.Height > 0 ? rect.Height / transformed.Height : 1;

        var placement = new PdfMatrix(
            scaleX, 0, 0, scaleY,
            rect.Left - (transformed.Left * scaleX),
            rect.Bottom - (transformed.Bottom * scaleY));

        PdfObjectId id = _form.Document.AddObject(PdfValue.Stream(appearance));

        canvas.Save();
        canvas.Transform(placement);
        canvas.DrawXObject(canvas.Resources.AddXObject(PdfValue.Reference(id)));
        canvas.Restore();
        return true;
    }

    private static PdfStream? FindAppearance(PdfDictionary annotation)
    {
        PdfValue normal = annotation.GetDictionary(PdfName.Get("AP"))?.Get(PdfName.Get("N")) ?? PdfValue.Null;

        if (normal.AsStream is { } stream)
            return stream;

        if (normal.AsDictionary is not { } states)
            return null;

        PdfName state = annotation.GetName(PdfName.Get("AS")) ?? PdfName.Get("Off");
        return states.GetStream(state) ?? (states.Count > 0 ? states.GetStream(states.Keys[0]) : null);
    }

    private void BuildTextAppearance(PdfFormField field, PdfDictionary widget)
    {
        PdfRectangle rect = PdfRectangle.FromArray(widget.GetArray(PdfName.Get("Rect")));
        if (rect.IsEmpty)
            return;

        // A viewer with a scripting engine runs the field's format action before painting; one
        // without shows whatever the appearance stream holds, so the formatting is baked in here.
        string text = field.DisplayValue;
        DefaultAppearance appearance = ReadDefaultAppearance(field);
        PdfDictionary resources = EnsureResources(appearance);

        double padding = 2;
        double width = Math.Max(0, rect.Width - (padding * 2));
        double height = rect.Height;
        double size = appearance.Size;

        if (size <= 0)
        {
            // An automatic size fills the box, leaving room for ascenders and descenders.
            size = field.IsMultiline ? 11 : Math.Clamp(height * 0.66, 4, 24);
        }

        var content = new StringBuilder(256);
        content.Append("/Tx BMC\nq\n");
        content.Append(CultureInfo.InvariantCulture, $"{padding:0.###} {padding:0.###} {width:0.###} {Math.Max(0, height - (padding * 2)):0.###} re\nW\nn\n");
        content.Append("BT\n");
        content.Append(appearance.Operators).Append('\n');
        content.Append(CultureInfo.InvariantCulture, $"/{appearance.FontName} {size:0.###} Tf\n");

        double lineHeight = size * 1.2;
        string[] lines = field.IsMultiline
            ? text.ReplaceLineEndings("\n").Split('\n')
            : [text.ReplaceLineEndings(" ")];

        double y = field.IsMultiline
            ? height - padding - size
            : ((height - size) / 2) + (size * 0.22);

        foreach (string line in lines)
        {
            double x = padding;
            if (!field.IsMultiline)
            {
                double textWidth = MeasureText(line, appearance, size);
                long quadding = field.Inherited(PdfName.Get("Q")).AsInteger;
                x = quadding switch
                {
                    1 => Math.Max(padding, (rect.Width - textWidth) / 2),
                    2 => Math.Max(padding, rect.Width - textWidth - padding),
                    _ => padding,
                };
            }

            content.Append(CultureInfo.InvariantCulture, $"1 0 0 1 {x:0.###} {y:0.###} Tm\n");
            content.Append(Escape(line)).Append(" Tj\n");
            y -= lineHeight;
        }

        content.Append("ET\nQ\nEMC\n");

        var stream = PdfStream.FromData(Encoding.UTF8.GetBytes(content.ToString()), compress: false);
        stream.Dictionary.Set(PdfName.Get("Type"), PdfValue.Name("XObject"));
        stream.Dictionary.Set(PdfName.Get("Subtype"), PdfValue.Name("Form"));
        stream.Dictionary.Set(PdfName.Get("FormType"), PdfValue.Integer(1));
        stream.Dictionary.Set(PdfName.Get("BBox"), PdfValue.Array(PdfArray.OfNumbers(0, 0, rect.Width, rect.Height)));
        stream.Dictionary.Set(PdfName.Get("Resources"), PdfValue.Dictionary(resources));

        PdfObjectId id = _form.Document.AddObject(PdfValue.Stream(stream));

        var normal = new PdfDictionary(1);
        normal.Set(PdfName.Get("N"), PdfValue.Reference(id));
        widget.Set(PdfName.Get("AP"), PdfValue.Dictionary(normal));
    }

    private double MeasureText(string text, DefaultAppearance appearance, double size)
    {
        if (appearance.Font is { } font)
        {
            double total = 0;
            foreach (char c in text)
                total += font.GetWidth(c);

            return total * size / 1000;
        }

        return Standard14Font.Helvetica.Measure(text, FontEncoding.WinAnsi, size);
    }

    private PdfDictionary EnsureResources(DefaultAppearance appearance)
    {
        PdfDictionary resources = _form.Root.GetDictionary(PdfName.Get("DR")) ?? CreateResources();
        PdfDictionary fonts = resources.GetDictionary(PdfName.Get("Font")) ?? CreateFonts(resources);

        if (!fonts.ContainsKey(PdfName.Get(appearance.FontName)))
        {
            var helvetica = new PdfDictionary(4);
            helvetica.Set(PdfName.Get("Type"), PdfValue.Name("Font"));
            helvetica.Set(PdfName.Get("Subtype"), PdfValue.Name("Type1"));
            helvetica.Set(PdfName.Get("BaseFont"), PdfValue.Name("Helvetica"));
            helvetica.Set(PdfName.Get("Encoding"), PdfValue.Name("WinAnsiEncoding"));

            PdfObjectId id = _form.Document.AddObject(PdfValue.Dictionary(helvetica));
            fonts.Set(PdfName.Get(appearance.FontName), PdfValue.Reference(id));
        }

        return resources;

        PdfDictionary CreateResources()
        {
            var created = new PdfDictionary(1);
            _form.Document.Attach(PdfValue.Dictionary(created));
            _form.Root.Set(PdfName.Get("DR"), PdfValue.Dictionary(created));
            return created;
        }

        PdfDictionary CreateFonts(PdfDictionary parent)
        {
            var created = new PdfDictionary(1);
            _form.Document.Attach(PdfValue.Dictionary(created));
            parent.Set(PdfName.Get("Font"), PdfValue.Dictionary(created));
            return created;
        }
    }

    private DefaultAppearance ReadDefaultAppearance(PdfFormField field)
    {
        string text = field.Inherited(PdfName.Get("DA")).AsTextString
                      ?? _form.Root.GetTextString(PdfName.Get("DA"))
                      ?? "0 g";

        string fontName = "Helv";
        double size = 0;
        var operators = new StringBuilder();

        string[] tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < tokens.Length; i++)
        {
            if (tokens[i] == "Tf" && i >= 2)
            {
                fontName = tokens[i - 2].TrimStart('/');
                _ = double.TryParse(tokens[i - 1], NumberStyles.Float, CultureInfo.InvariantCulture, out size);
                continue;
            }

            // Everything that is not the font selection is colour and spacing state worth keeping.
            if (i + 1 < tokens.Length && tokens[i + 1] == "Tf")
                continue;

            if (i + 2 < tokens.Length && tokens[i + 2] == "Tf")
                continue;

            operators.Append(tokens[i]).Append(' ');
        }

        LoadedFont? font = null;
        if (_form.Root.GetDictionary(PdfName.Get("DR"))?.GetDictionary(PdfName.Get("Font"))
                ?.GetDictionary(PdfName.Get(fontName)) is { } dictionary)
        {
            font = LoadedFont.Read(dictionary);
        }

        return new DefaultAppearance(fontName, size, operators.ToString().Trim(), font);
    }

    private static string Escape(string text)
    {
        if (PdfTextEncoding.TryEncodePdfDoc(text, out byte[] encoded))
        {
            var builder = new StringBuilder(encoded.Length + 2);
            builder.Append('(');
            foreach (byte b in encoded)
            {
                if (b is (byte)'(' or (byte)')' or (byte)'\\')
                    builder.Append('\\');

                builder.Append((char)b);
            }

            return builder.Append(')').ToString();
        }

        byte[] utf16 = PdfTextEncoding.EncodeTextString(text);
        var hex = new StringBuilder((utf16.Length * 2) + 2);
        hex.Append('<');
        foreach (byte b in utf16)
            hex.Append(b.ToString("X2", CultureInfo.InvariantCulture));

        return hex.Append('>').ToString();
    }

    private readonly record struct DefaultAppearance(string FontName, double Size, string Operators, LoadedFont? Font);
}
