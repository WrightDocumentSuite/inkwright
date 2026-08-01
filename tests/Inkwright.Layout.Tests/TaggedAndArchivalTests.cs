using System.Text;
using Inkwright.Annotations;
using Inkwright.Cos;
using Inkwright.Fonts;
using Inkwright.Layout;
using Xunit;

namespace Inkwright.Layout.Tests;

public class TaggedAndArchivalTests
{
    [Fact]
    public void BuildsAStructureTreeWhileDrawing()
    {
        byte[] bytes = Report.Create()
            .Page(page =>
            {
                page.Tagged("en-GB");
                page.Content().Column(column =>
                {
                    column.Item().Text("A heading", TextStyle.Default.FontSize(18).Bold());
                    column.Item().Text("A paragraph of body text.");
                    column.Item().Table(table =>
                    {
                        table.RelativeColumns(2);
                        table.Header().Text("Name").Text("Value");
                        table.Row().Text("Alpha").Text("1");
                    });
                });
            })
            .ToArray();

        using PdfDocument document = PdfDocument.Load(bytes);

        PdfDictionary root = document.Catalog.GetDictionary(PdfName.Get("StructTreeRoot"))!;
        Assert.Equal("StructTreeRoot", root.GetName(PdfName.Get("Type"))!.Value);
        Assert.True(document.Catalog.GetDictionary(PdfName.Get("MarkInfo"))!.GetBoolean(PdfName.Get("Marked")));
        Assert.Equal("en-GB", document.Catalog.GetTextString(PdfName.Get("Lang")));

        PdfDictionary documentElement = root.GetArray(PdfName.Get("K"))!.GetDictionary(0)!;
        Assert.Equal("Document", documentElement.GetName(PdfName.Get("S"))!.Value);

        List<string> tags = [];
        Collect(documentElement, tags, document);

        Assert.Contains("P", tags);
        Assert.Contains("Table", tags);
        Assert.Contains("TR", tags);
        Assert.Contains("TH", tags);
        Assert.Contains("TD", tags);

        // Every page in a tagged document points back into the parent tree.
        Assert.True(document.Pages[0].Dictionary.ContainsKey(PdfName.Get("StructParents")));
        Assert.NotNull(root.GetDictionary(PdfName.Get("ParentTree")));

        string content = Encoding.ASCII.GetString(document.Pages[0].GetContent());
        Assert.Contains("/P <</MCID 0>> BDC", content, StringComparison.Ordinal);
        Assert.Contains("EMC", content, StringComparison.Ordinal);
    }

    [Fact]
    public void TagsImagesWithTheirDescription()
    {
        byte[] png = BuildPng();

        using var source = PdfDocument.Create();
        Images.PdfImage image = Images.PdfImage.Load(source, png);

        var report = Report.Create().Page(page =>
        {
            page.Tagged();
            page.Content().Element(new Elements.ImageElement
            {
                Image = image,
                AlternateText = "A gradient used as a placeholder",
            });
        });

        report.Render(source);
        using PdfDocument document = PdfDocument.Load(source.ToArray());

        PdfDictionary root = document.Catalog.GetDictionary(PdfName.Get("StructTreeRoot"))!;
        PdfDictionary documentElement = root.GetArray(PdfName.Get("K"))!.GetDictionary(0)!;
        PdfDictionary figure = documentElement.Get(PdfName.Get("K")).AsDictionary!;

        Assert.Equal("Figure", figure.GetName(PdfName.Get("S"))!.Value);
        Assert.Equal("A gradient used as a placeholder", figure.GetTextString(PdfName.Get("Alt")));
    }

    [Fact]
    public void ProducesAnArchivalDocument()
    {
        string? fontPath = SystemFonts.Find("Arial") ?? SystemFonts.Find("DejaVuSans");
        Assert.SkipWhen(fontPath is null, "PDF/A needs an embeddable font and none was found.");

        using var document = PdfDocument.Create();
        PdfFont font = document.Fonts.Embed(fontPath!);

        Report.Create()
            .Page(page =>
            {
                page.Tagged("en");
                page.DefaultTextStyle(TextStyle.Default.Font(font));
                page.Content().Text("An archival document keeps its fonts with it.");
            })
            .Render(document);

        document.Info.Title = "Archival sample";
        document.Info.Author = "Inkwright";

        PdfAProfile.Apply(document, PdfAConformance.A2B, new PdfAOptions { Language = "en" });

        using PdfDocument reloaded = PdfDocument.Load(document.ToArray());

        PdfArray intents = reloaded.Catalog.GetArray(PdfName.Get("OutputIntents"))!;
        PdfDictionary intent = intents.GetDictionary(0)!;
        Assert.Equal("GTS_PDFA1", intent.GetName(PdfName.Get("S"))!.Value);

        PdfStream profile = intent.GetStream(PdfName.Get("DestOutputProfile"))!;
        Assert.Equal(3, profile.Dictionary.GetInteger(PdfName.Get("N")));
        Assert.True(profile.Decode().Length > 200);
        Assert.Equal("acsp", Encoding.ASCII.GetString(profile.Decode().Span.Slice(36, 4)));

        string xmp = Encoding.UTF8.GetString(reloaded.Catalog.GetStream(PdfName.Get("Metadata"))!.DecodedSpan());
        Assert.Contains("<pdfaid:part>2</pdfaid:part>", xmp, StringComparison.Ordinal);
        Assert.Contains("<pdfaid:conformance>B</pdfaid:conformance>", xmp, StringComparison.Ordinal);
        Assert.Contains("Archival sample", xmp, StringComparison.Ordinal);

        Assert.Empty(PdfAProfile.Validate(reloaded, PdfAConformance.A2B));
    }

    [Fact]
    public void JudgesCommentsAgainstTheArchivalRules()
    {
        string? fontPath = SystemFonts.Find("Arial") ?? SystemFonts.Find("DejaVuSans");
        Assert.SkipWhen(fontPath is null, "PDF/A needs an embeddable font and none was found.");

        using var document = PdfDocument.Create();
        PdfFont font = document.Fonts.Embed(fontPath!);

        Report.Create()
            .Page(page =>
            {
                page.DefaultTextStyle(TextStyle.Default.Font(font));
                page.Content().Text("A document that gets reviewed before it is archived.");
            })
            .Render(document);

        PdfPage first = document.Pages[0];
        PdfTextAnnotation note = first.Annotations.AddNote(new PdfPoint(500, 700), "Looks right", "Reviewer");
        note.Reply("Agreed", "Author");

        // A comment written by this library is archival as it stands: it prints, it is not hidden,
        // and it brings its own drawing.
        PdfAProfile.Apply(document, PdfAConformance.A2B, new PdfAOptions { Language = "en" });
        Assert.Empty(PdfAProfile.Validate(document, PdfAConformance.A2B));

        note.IsHidden = true;
        Assert.Contains(
            PdfAProfile.Validate(document, PdfAConformance.A2B),
            problem => problem.Message.Contains("Hidden", StringComparison.Ordinal) && problem.Clause == "6.3.2");

        note.IsHidden = false;
        first.Annotations.RawArray.Add(PdfValue.Reference(document.AddObject(PdfValue.Dictionary(Bare()))));

        IReadOnlyList<PdfAProblem> problems = PdfAProfile.Validate(document, PdfAConformance.A2B);
        Assert.Contains(problems, problem => problem.Message.Contains("no appearance stream", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem.Message.Contains("no F entry", StringComparison.Ordinal));

        static PdfDictionary Bare()
        {
            var square = new PdfDictionary(3);
            square.Set(PdfName.Get("Type"), PdfValue.Name(PdfName.Get("Annot")));
            square.Set(PdfName.Get("Subtype"), PdfValue.Name(PdfName.Get("Square")));
            square.Set(PdfName.Get("Rect"), PdfValue.Array(PdfArray.OfNumbers(40, 40, 200, 90)));
            return square;
        }
    }

    [Fact]
    public void RefusesToAttachAnythingButPdfToAPartTwoDocument()
    {
        string? fontPath = SystemFonts.Find("Arial") ?? SystemFonts.Find("DejaVuSans");
        Assert.SkipWhen(fontPath is null, "PDF/A needs an embeddable font and none was found.");

        using var document = PdfDocument.Create();
        PdfFont font = document.Fonts.Embed(fontPath!);

        Report.Create()
            .Page(page =>
            {
                page.DefaultTextStyle(TextStyle.Default.Font(font));
                page.Content().Text("An invoice with its data attached.");
            })
            .Render(document);

        document.Attachments.Add("invoice.xml", "<invoice/>"u8.ToArray(), "text/xml", "The figures");

        // Part 2 only carries attachments that are themselves PDF/A; part 3 exists for this case.
        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(
            () => PdfAProfile.Apply(document, PdfAConformance.A2B));

        Assert.Contains("PDF/A-3", failure.Message, StringComparison.Ordinal);

        PdfAProfile.Apply(document, PdfAConformance.A3B);
        Assert.Empty(PdfAProfile.Validate(document, PdfAConformance.A3B));
    }

    [Fact]
    public void RefusesToClaimArchivalStatusWithoutEmbeddedFonts()
    {
        using var document = PdfDocument.Create();
        Report.Create()
            .Page(page => page.Content().Text("Helvetica is not embedded anywhere."))
            .Render(document);

        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(
            () => PdfAProfile.Apply(document, PdfAConformance.A2B));

        Assert.Contains("not embedded", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Helvetica", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CarriesAFacturXInvoiceAlongsideThePages()
    {
        byte[] invoice = Encoding.UTF8.GetBytes(
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?><rsm:CrossIndustryInvoice " +
            "xmlns:rsm=\"urn:un:unece:uncefact:data:standard:CrossIndustryInvoice:100\" />");

        using var document = PdfDocument.Create();
        Report.Create()
            .Page(page => page.Content().Text("Invoice 2026-014"))
            .Render(document);

        document.Attachments.AddFacturX(invoice);

        using PdfDocument reloaded = PdfDocument.Load(document.ToArray());

        Assert.Contains("factur-x.xml", reloaded.Attachments.Names);
        Assert.True(invoice.AsSpan().SequenceEqual(reloaded.Attachments.Read("factur-x.xml")));

        PdfArray associated = reloaded.Catalog.GetArray(PdfName.Get("AF"))!;
        PdfDictionary spec = associated.GetDictionary(0)!;
        Assert.Equal("Data", spec.GetName(PdfName.Get("AFRelationship"))!.Value);
        Assert.Equal("factur-x.xml", spec.GetTextString(PdfName.Get("UF")));

        string xmp = Encoding.UTF8.GetString(reloaded.Catalog.GetStream(PdfName.Get("Metadata"))!.DecodedSpan());
        Assert.Contains("fx:DocumentFileName", xmp, StringComparison.Ordinal);
        Assert.Contains("EN 16931", xmp, StringComparison.Ordinal);

        // The fx: namespace belongs to nobody's predefined schema, so clause 6.6.2.3.2 wants the
        // packet to explain it. Without that a hybrid invoice fails the one rule it exists for.
        Assert.Contains("pdfaExtension:schemas", xmp, StringComparison.Ordinal);
        Assert.Contains("<pdfaSchema:prefix>fx</pdfaSchema:prefix>", xmp, StringComparison.Ordinal);
    }

    [Fact]
    public void AFacturXInvoiceIsArchivalUnderPartThree()
    {
        string? fontPath = SystemFonts.Find("Arial") ?? SystemFonts.Find("DejaVuSans");
        Assert.SkipWhen(fontPath is null, "PDF/A needs an embeddable font and none was found.");

        using var document = PdfDocument.Create();
        PdfFont font = document.Fonts.Embed(fontPath!);

        Report.Create()
            .Page(page =>
            {
                page.DefaultTextStyle(TextStyle.Default.Font(font));
                page.Content().Text("Invoice 2026-014");
            })
            .Render(document);

        document.Attachments.AddFacturX("<invoice/>"u8.ToArray());
        PdfAProfile.Apply(document, PdfAConformance.A3B, new PdfAOptions { Language = "en" });

        Assert.Empty(PdfAProfile.Validate(document, PdfAConformance.A3B));
        Assert.Empty(PdfAProfile.Validate(document.ToArray(), PdfAConformance.A3B));
    }

    private static void Collect(PdfDictionary element, List<string> tags, PdfDocument document)
    {
        if (element.GetName(PdfName.Get("S")) is { } tag)
            tags.Add(tag.Value);

        PdfValue kids = element.Get(PdfName.Get("K"));

        if (kids.AsArray is { } array)
        {
            for (int i = 0; i < array.Count; i++)
            {
                if (array.Get(i).AsDictionary is { } child && child.ContainsKey(PdfName.Get("S")))
                    Collect(child, tags, document);
            }
        }
        else if (kids.AsDictionary is { } single && single.ContainsKey(PdfName.Get("S")))
        {
            Collect(single, tags, document);
        }
    }

    private static byte[] BuildPng()
    {
        using var buffer = new MemoryStream();
        buffer.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        byte[] header = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0, 4), 4);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4, 4), 4);
        header[8] = 8;
        header[9] = 2;

        byte[] raw = new byte[4 * ((4 * 3) + 1)];
        using var compressed = new MemoryStream();
        using (var deflate = new System.IO.Compression.ZLibStream(compressed, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
            deflate.Write(raw);

        WriteChunk(buffer, "IHDR"u8, header);
        WriteChunk(buffer, "IDAT"u8, compressed.ToArray());
        WriteChunk(buffer, "IEND"u8, []);
        return buffer.ToArray();
    }

    private static void WriteChunk(Stream stream, ReadOnlySpan<byte> type, ReadOnlySpan<byte> payload)
    {
        Span<byte> length = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, payload.Length);
        stream.Write(length);
        stream.Write(type);
        stream.Write(payload);

        uint crc = 0xFFFFFFFF;
        foreach (byte b in type)
            crc = Step(crc, b);

        foreach (byte b in payload)
            crc = Step(crc, b);

        Span<byte> checksum = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(checksum, crc ^ 0xFFFFFFFF);
        stream.Write(checksum);

        static uint Step(uint crc, byte value)
        {
            crc ^= value;
            for (int i = 0; i < 8; i++)
                crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;

            return crc;
        }
    }
}
