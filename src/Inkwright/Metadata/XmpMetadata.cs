using System.Globalization;
using System.Security;
using System.Text;
using Inkwright.Cos;

namespace Inkwright.Metadata;

/// <summary>
/// The XMP packet a document carries alongside its information dictionary. PDF/A requires the two
/// to agree, so this builds the packet from the same values rather than leaving the caller to keep
/// them in step by hand.
/// </summary>
public sealed class XmpMetadata
{
    private readonly List<string> _extraSchemas = [];
    private readonly List<XmpExtensionSchema> _extensions = [];

    /// <summary>The document title.</summary>
    public string? Title { get; set; }

    /// <summary>Who wrote the document.</summary>
    public string? Author { get; set; }

    /// <summary>What the document is about.</summary>
    public string? Subject { get; set; }

    /// <summary>Keywords, comma separated.</summary>
    public string? Keywords { get; set; }

    /// <summary>The application that produced the original document.</summary>
    public string? Creator { get; set; }

    /// <summary>The application that converted the document to PDF.</summary>
    public string? Producer { get; set; }

    /// <summary>When the document was created.</summary>
    public DateTimeOffset? CreationDate { get; set; }

    /// <summary>When the document was last changed.</summary>
    public DateTimeOffset? ModificationDate { get; set; }

    /// <summary>The PDF/A part number, or <see langword="null"/> when the document claims no conformance.</summary>
    public int? PdfAPart { get; set; }

    /// <summary>The PDF/A conformance level, such as <c>B</c>.</summary>
    /// <remarks>
    /// Part 4 narrows this to <c>E</c> or <c>F</c> and makes it optional: a plain PDF/A-4 file
    /// shall carry no level at all (ISO 19005-4 §6.7.3).
    /// </remarks>
    public string? PdfAConformance { get; set; }

    /// <summary>
    /// Four-digit publication or revision year of the claimed PDF/A part. Part 4 requires it;
    /// parts 1 to 3 have no such property and leave it alone.
    /// </summary>
    public int? PdfARevision { get; set; }

    /// <summary>
    /// The PDF/UA part number, or <see langword="null"/> when the document claims no accessibility
    /// conformance. A document may claim both this and a PDF/A level; the two are independent.
    /// </summary>
    public int? PdfUaPart { get; set; }

    /// <summary>
    /// Four-digit publication or revision year of the claimed PDF/UA specification. PDF/UA-2
    /// requires it; when omitted for part 2 the current 2024 publication year is written.
    /// </summary>
    public int? PdfUaRevision { get; set; }

    /// <summary>The document's language, as a BCP 47 tag.</summary>
    public string? Language { get; set; }

    /// <summary>Adds a schema description, which PDF/A requires for properties outside the standard set.</summary>
    /// <param name="rdfDescription">A complete <c>rdf:Description</c> element.</param>
    public void AddSchema(string rdfDescription)
    {
        ArgumentException.ThrowIfNullOrEmpty(rdfDescription);
        _extraSchemas.Add(rdfDescription);
    }

    /// <summary>
    /// Declares a schema of one's own. PDF/A does not forbid metadata it has never heard of, but it
    /// does insist that the file explain it: a property nobody can interpret is not preservation.
    /// The description goes into the packet as a <c>pdfaExtension:schemas</c> entry.
    /// </summary>
    /// <param name="schema">The schema to describe and register.</param>
    public void AddExtensionSchema(XmpExtensionSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        _extensions.Add(schema);
    }

    /// <summary>Copies the values from a document information dictionary.</summary>
    /// <param name="info">The information dictionary to read.</param>
    public void CopyFrom(PdfDocumentInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);

        Title = info.Title;
        Author = info.Author;
        Subject = info.Subject;
        Keywords = info.Keywords;
        Creator = info.Creator;
        Producer = info.Producer;
        CreationDate = info.CreationDate;
        ModificationDate = info.ModificationDate;
    }

    /// <summary>Builds the XMP packet.</summary>
    public string Build()
    {
        var xmp = new StringBuilder(2048);

        xmp.Append("<?xpacket begin=\"\uFEFF\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>\n");
        xmp.Append("<x:xmpmeta xmlns:x=\"adobe:ns:meta/\" x:xmptk=\"Inkwright\">\n");
        xmp.Append("  <rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">\n");

        xmp.Append("    <rdf:Description rdf:about=\"\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\">\n");
        AppendAlt(xmp, "dc:title", Title);
        AppendSeq(xmp, "dc:creator", Author);
        AppendAlt(xmp, "dc:description", Subject);
        AppendBag(xmp, "dc:subject", Keywords);
        xmp.Append("      <dc:format>application/pdf</dc:format>\n");
        if (!string.IsNullOrEmpty(Language))
            xmp.Append(CultureInfo.InvariantCulture, $"      <dc:language><rdf:Bag><rdf:li>{Escape(Language)}</rdf:li></rdf:Bag></dc:language>\n");

        xmp.Append("    </rdf:Description>\n");

        xmp.Append("    <rdf:Description rdf:about=\"\" xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\">\n");
        AppendSimple(xmp, "xmp:CreatorTool", Creator);
        AppendDate(xmp, "xmp:CreateDate", CreationDate);
        AppendDate(xmp, "xmp:ModifyDate", ModificationDate);
        AppendDate(xmp, "xmp:MetadataDate", ModificationDate ?? CreationDate);
        xmp.Append("    </rdf:Description>\n");

        xmp.Append("    <rdf:Description rdf:about=\"\" xmlns:pdf=\"http://ns.adobe.com/pdf/1.3/\">\n");
        AppendSimple(xmp, "pdf:Producer", Producer);
        AppendSimple(xmp, "pdf:Keywords", Keywords);
        xmp.Append("    </rdf:Description>\n");

        if (PdfAPart is { } part)
        {
            xmp.Append("    <rdf:Description rdf:about=\"\" xmlns:pdfaid=\"http://www.aiim.org/pdfa/ns/id/\">\n");
            xmp.Append(CultureInfo.InvariantCulture, $"      <pdfaid:part>{part}</pdfaid:part>\n");

            if (PdfARevision is { } revision)
                xmp.Append(CultureInfo.InvariantCulture, $"      <pdfaid:rev>{revision}</pdfaid:rev>\n");

            if (!string.IsNullOrEmpty(PdfAConformance))
                xmp.Append(CultureInfo.InvariantCulture, $"      <pdfaid:conformance>{Escape(PdfAConformance)}</pdfaid:conformance>\n");

            xmp.Append("    </rdf:Description>\n");
        }

        if (PdfUaPart is { } accessibility)
        {
            xmp.Append("    <rdf:Description rdf:about=\"\" xmlns:pdfuaid=\"http://www.aiim.org/pdfua/ns/id/\">\n");
            xmp.Append(CultureInfo.InvariantCulture, $"      <pdfuaid:part>{accessibility}</pdfuaid:part>\n");
            int? revision = PdfUaRevision ?? (accessibility == 2 ? 2024 : null);
            if (revision is { } year)
                xmp.Append(CultureInfo.InvariantCulture, $"      <pdfuaid:rev>{year:D4}</pdfuaid:rev>\n");
            xmp.Append("    </rdf:Description>\n");
        }

        foreach (string schema in _extraSchemas)
            xmp.Append("    ").Append(schema).Append('\n');

        AppendExtensionSchemas(xmp);

        xmp.Append("  </rdf:RDF>\n");
        xmp.Append("</x:xmpmeta>\n");

        // The trailing whitespace is the padding readers expect so a packet can be edited in place.
        for (int i = 0; i < 20; i++)
            xmp.Append("                                                                                                    \n");

        xmp.Append("<?xpacket end=\"w\"?>");
        return xmp.ToString();
    }

    /// <summary>Writes the packet into a document as its metadata stream.</summary>
    /// <param name="document">The document to update.</param>
    public void ApplyTo(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        byte[] packet = Encoding.UTF8.GetBytes(Build());

        // A metadata stream must stay readable to tools that do not parse PDF, so it is never filtered.
        var stream = PdfStream.FromData(packet, compress: false);
        stream.Dictionary.Set(PdfName.Get("Type"), PdfValue.Name("Metadata"));
        stream.Dictionary.Set(PdfName.Get("Subtype"), PdfValue.Name("XML"));

        PdfObjectId id = document.AddObject(PdfValue.Stream(stream));
        document.Catalog.Set(PdfName.Get("Metadata"), PdfValue.Reference(id));
        document.MarkChanged(document.CatalogId);
    }

    /// <summary>Writes the container schema of Tables 2 to 5, which describes the schemas of one's own.</summary>
    private void AppendExtensionSchemas(StringBuilder xmp)
    {
        if (_extensions.Count == 0)
            return;

        xmp.Append("""
                <rdf:Description rdf:about=""
                    xmlns:pdfaExtension="http://www.aiim.org/pdfa/ns/extension/"
                    xmlns:pdfaSchema="http://www.aiim.org/pdfa/ns/schema#"
                    xmlns:pdfaProperty="http://www.aiim.org/pdfa/ns/property#">
                  <pdfaExtension:schemas>
                    <rdf:Bag>

            """);

        foreach (XmpExtensionSchema schema in _extensions)
            AppendExtensionSchema(xmp, schema);

        xmp.Append("""
                    </rdf:Bag>
                  </pdfaExtension:schemas>
                </rdf:Description>

            """);
    }

    private static void AppendExtensionSchema(StringBuilder xmp, XmpExtensionSchema schema)
    {
        xmp.Append("          <rdf:li rdf:parseType=\"Resource\">\n");
        xmp.Append(CultureInfo.InvariantCulture, $"            <pdfaSchema:schema>{Escape(schema.Name)}</pdfaSchema:schema>\n");
        xmp.Append(CultureInfo.InvariantCulture, $"            <pdfaSchema:namespaceURI>{Escape(schema.NamespaceUri)}</pdfaSchema:namespaceURI>\n");
        xmp.Append(CultureInfo.InvariantCulture, $"            <pdfaSchema:prefix>{Escape(schema.Prefix)}</pdfaSchema:prefix>\n");
        xmp.Append("            <pdfaSchema:property>\n              <rdf:Seq>\n");

        foreach (XmpSchemaProperty property in schema.Properties)
        {
            xmp.Append("                <rdf:li rdf:parseType=\"Resource\">\n");
            xmp.Append(CultureInfo.InvariantCulture, $"                  <pdfaProperty:name>{Escape(property.Name)}</pdfaProperty:name>\n");
            xmp.Append(CultureInfo.InvariantCulture, $"                  <pdfaProperty:valueType>{Escape(property.ValueType)}</pdfaProperty:valueType>\n");
            xmp.Append(CultureInfo.InvariantCulture, $"                  <pdfaProperty:category>{Escape(property.Category)}</pdfaProperty:category>\n");
            xmp.Append(CultureInfo.InvariantCulture, $"                  <pdfaProperty:description>{Escape(property.Description)}</pdfaProperty:description>\n");
            xmp.Append("                </rdf:li>\n");
        }

        xmp.Append("              </rdf:Seq>\n            </pdfaSchema:property>\n          </rdf:li>\n");
    }

    private static void AppendSimple(StringBuilder xmp, string name, string? value)
    {
        if (!string.IsNullOrEmpty(value))
            xmp.Append(CultureInfo.InvariantCulture, $"      <{name}>{Escape(value)}</{name}>\n");
    }

    private static void AppendAlt(StringBuilder xmp, string name, string? value)
    {
        if (string.IsNullOrEmpty(value))
            return;

        xmp.Append(CultureInfo.InvariantCulture,
            $"      <{name}><rdf:Alt><rdf:li xml:lang=\"x-default\">{Escape(value)}</rdf:li></rdf:Alt></{name}>\n");
    }

    private static void AppendSeq(StringBuilder xmp, string name, string? value)
    {
        if (string.IsNullOrEmpty(value))
            return;

        xmp.Append(CultureInfo.InvariantCulture, $"      <{name}><rdf:Seq>");
        foreach (string part in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            xmp.Append(CultureInfo.InvariantCulture, $"<rdf:li>{Escape(part)}</rdf:li>");

        xmp.Append(CultureInfo.InvariantCulture, $"</rdf:Seq></{name}>\n");
    }

    private static void AppendBag(StringBuilder xmp, string name, string? value)
    {
        if (string.IsNullOrEmpty(value))
            return;

        xmp.Append(CultureInfo.InvariantCulture, $"      <{name}><rdf:Bag>");
        foreach (string part in value.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            xmp.Append(CultureInfo.InvariantCulture, $"<rdf:li>{Escape(part)}</rdf:li>");

        xmp.Append(CultureInfo.InvariantCulture, $"</rdf:Bag></{name}>\n");
    }

    private static void AppendDate(StringBuilder xmp, string name, DateTimeOffset? value)
    {
        if (value is { } moment)
        {
            xmp.Append(CultureInfo.InvariantCulture,
                $"      <{name}>{moment.ToString("yyyy-MM-ddTHH:mm:ssK", CultureInfo.InvariantCulture)}</{name}>\n");
        }
    }

    private static string Escape(string value) => SecurityElement.Escape(value) ?? string.Empty;
}
