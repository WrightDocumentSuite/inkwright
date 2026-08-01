using Inkwright.Cos;

namespace Inkwright.Forms;

/// <summary>Creating fields on a page, for documents that are being built rather than filled in.</summary>
public static class PdfFormBuilder
{
    extension(PdfForm form)
    {
        /// <summary>Adds a single-line or multi-line text field.</summary>
        /// <param name="page">The page to put the widget on.</param>
        /// <param name="bounds">Where the widget sits, in page space.</param>
        /// <param name="name">The field name.</param>
        /// <param name="value">The initial value.</param>
        /// <param name="multiline">Whether the field accepts more than one line.</param>
        /// <param name="fontSize">The font size, or zero to fit the box.</param>
        public PdfFormField AddTextField(
            PdfPage page,
            PdfRectangle bounds,
            string name,
            string? value = null,
            bool multiline = false,
            double fontSize = 0)
        {
            PdfDictionary field = form.CreateWidget(page, bounds, name, "Tx");

            if (multiline)
                field.Set(PdfName.Get("Ff"), PdfValue.Integer(1 << 12));

            field.Set(PdfName.Get("DA"), PdfValue.TextString($"/Helv {fontSize.ToString(System.Globalization.CultureInfo.InvariantCulture)} Tf 0 g"));

            PdfFormField created = form.Register(field, name);
            if (value is not null)
                created.SetValue(value);
            else
                form.AppearanceGenerator.Update(created);

            return created;
        }

        /// <summary>Adds a tick box.</summary>
        /// <param name="page">The page to put the widget on.</param>
        /// <param name="bounds">Where the widget sits, in page space.</param>
        /// <param name="name">The field name.</param>
        /// <param name="isChecked">Whether the box starts ticked.</param>
        public PdfFormField AddCheckBox(PdfPage page, PdfRectangle bounds, string name, bool isChecked = false)
        {
            PdfDictionary field = form.CreateWidget(page, bounds, name, "Btn");

            var on = PdfStream.FromData(BuildTick(bounds), compress: false);
            Describe(on, bounds);
            var off = PdfStream.FromData("q Q"u8.ToArray(), compress: false);
            Describe(off, bounds);

            var normal = new PdfDictionary(2);
            normal.Set(PdfName.Get("Yes"), PdfValue.Reference(form.Document.AddObject(PdfValue.Stream(on))));
            normal.Set(PdfName.Get("Off"), PdfValue.Reference(form.Document.AddObject(PdfValue.Stream(off))));

            var appearance = new PdfDictionary(1);
            appearance.Set(PdfName.Get("N"), PdfValue.Dictionary(normal));
            field.Set(PdfName.Get("AP"), PdfValue.Dictionary(appearance));
            field.Set(PdfName.Get("AS"), PdfValue.Name("Off"));

            PdfFormField created = form.Register(field, name);
            created.IsChecked = isChecked;
            return created;
        }

        /// <summary>Adds a drop-down list.</summary>
        /// <param name="page">The page to put the widget on.</param>
        /// <param name="bounds">Where the widget sits, in page space.</param>
        /// <param name="name">The field name.</param>
        /// <param name="options">The options to offer.</param>
        /// <param name="value">The initial value.</param>
        public PdfFormField AddComboBox(
            PdfPage page, PdfRectangle bounds, string name, IEnumerable<string> options, string? value = null)
        {
            ArgumentNullException.ThrowIfNull(options);

            PdfDictionary field = form.CreateWidget(page, bounds, name, "Ch");
            field.Set(PdfName.Get("Ff"), PdfValue.Integer(1 << 17));
            field.Set(PdfName.Get("DA"), PdfValue.TextString("/Helv 0 Tf 0 g"));

            var list = new PdfArray();
            foreach (string option in options)
                list.Add(PdfValue.TextString(option));

            field.Set(PdfName.Get("Opt"), PdfValue.Array(list));

            PdfFormField created = form.Register(field, name);
            created.SetValue(value ?? (list.Count > 0 ? list.Get(0).AsTextString : null));
            return created;
        }
    }

    private static void Describe(PdfStream stream, PdfRectangle bounds)
    {
        stream.Dictionary.Set(PdfName.Get("Type"), PdfValue.Name("XObject"));
        stream.Dictionary.Set(PdfName.Get("Subtype"), PdfValue.Name("Form"));
        stream.Dictionary.Set(PdfName.Get("BBox"), PdfValue.Array(PdfArray.OfNumbers(0, 0, bounds.Width, bounds.Height)));
    }

    private static byte[] BuildTick(PdfRectangle bounds)
    {
        double inset = Math.Min(bounds.Width, bounds.Height) * 0.25;
        double width = bounds.Width - (inset * 2);
        double height = bounds.Height - (inset * 2);

        return System.Text.Encoding.ASCII.GetBytes(string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"q 0 g 1.4 w 1 J 1 j\n{inset:0.##} {inset + (height / 2):0.##} m\n" +
            $"{inset + (width * 0.4):0.##} {inset:0.##} l\n" +
            $"{inset + width:0.##} {inset + height:0.##} l\nS Q\n"));
    }
}
