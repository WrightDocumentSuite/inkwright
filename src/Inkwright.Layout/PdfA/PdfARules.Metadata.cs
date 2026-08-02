using System.Globalization;
using System.Xml.Linq;
using Inkwright.Cos;

namespace Inkwright.Layout;

internal static partial class PdfARules
{
    /// <summary>The schemas every conforming reader is expected to know without being told.</summary>
    private static readonly string[] _knownNamespaces =
    [
        "http://purl.org/dc/elements/1.1/",
        "http://ns.adobe.com/xap/1.0/",
        "http://ns.adobe.com/xap/1.0/mm/",
        "http://ns.adobe.com/xap/1.0/rights/",
        "http://ns.adobe.com/xap/1.0/bj/",
        "http://ns.adobe.com/xap/1.0/t/pg/",
        "http://ns.adobe.com/xmp/1.0/DynamicMedia/",
        "http://ns.adobe.com/pdf/1.3/",
        "http://ns.adobe.com/pdfx/1.3/",
        "http://ns.adobe.com/photoshop/1.0/",
        "http://ns.adobe.com/tiff/1.0/",
        "http://ns.adobe.com/exif/1.0/",
        "http://ns.adobe.com/camera-raw-settings/1.0/",
        "http://www.aiim.org/pdfa/ns/id/",
        "http://www.aiim.org/pdfa/ns/extension/",
        "http://www.aiim.org/pdfa/ns/schema#",
        "http://www.aiim.org/pdfa/ns/property#",
        "http://www.aiim.org/pdfa/ns/type#",
        "http://www.aiim.org/pdfa/ns/field#",
        "http://www.w3.org/1999/02/22-rdf-syntax-ns#",
        "adobe:ns:meta/",
        "http://ns.adobe.com/xap/1.0/sType/ResourceEvent#",
        "http://ns.adobe.com/xap/1.0/sType/ResourceRef#",
        "http://ns.adobe.com/xap/1.0/sType/Version#",
        "http://ns.adobe.com/xap/1.0/sType/Job#",
    ];

    /// <summary>Clause 6.7 of parts 1 and 4, clause 6.6 of parts 2 and 3: XMP metadata.</summary>
    internal static void CheckMetadata(PdfAContext context)
    {
        string clause = context.Clause("6.7.2", "6.6.2.1", "6.7.2.1");
        PdfStream? catalogMetadata = context.Document.Catalog.Get(PdfNames.Metadata).AsStream;
        if (catalogMetadata is null)
        {
            context.Report(
                clause,
                "The catalogue has no Metadata stream. A file that cannot say what it is has nothing " +
                "to preserve its identity with.");
        }

        List<PdfStream> streams = MetadataStreams(context, catalogMetadata, clause);
        PdfAXmpPacket? catalogPacket = null;
        for (int i = 0; i < streams.Count; i++)
        {
            PdfStream stream = streams[i];
            bool catalog = ReferenceEquals(stream, catalogMetadata);
            CheckMetadataStreamDeclaration(context, stream, clause);

            if (context.Part == 1 && stream.Dictionary.ContainsKey(PdfNames.Filter))
            {
                context.Report(
                    "6.7.2",
                    "A Metadata stream is filtered. Part 1 wants it in plain sight, so that a tool " +
                    "which knows nothing about PDF can still read what the file claims to be.");
            }

            if (!context.Validation.TryReadXmp(
                    stream,
                    out PdfAXmpPacket? packet,
                    out string? problem))
            {
                context.Report(
                    clause,
                    $"The {(catalog ? "catalogue" : "file-level")} XMP metadata is unusable: {problem}");
                continue;
            }

            if (!PdfAXmpStructure.TryGetDescriptions(
                    packet!.Document,
                    out _,
                    out string? structureProblem))
            {
                context.Report(
                    clause,
                    $"The {(catalog ? "catalogue" : "file-level")} Metadata stream is not a valid " +
                    $"XMP packet: {structureProblem}");
                continue;
            }

            if (catalog)
                catalogPacket = packet;
        }

        if (catalogPacket is null)
            return;

        CheckPacketHeader(context, catalogPacket.Text, clause);
        CheckPacketContents(context, catalogPacket.Document);
        CheckDocumentInformation(context, catalogPacket.Text);
    }

    private static List<PdfStream> MetadataStreams(
        PdfAContext context,
        PdfStream? catalogMetadata,
        string clause)
    {
        var result = new List<PdfStream>();
        var seen = new HashSet<PdfStream>(ReferenceEqualityComparer.Instance);
        Add(catalogMetadata);

        foreach (PdfDictionary dictionary in context.FileDictionaries)
        {
            if (!dictionary.TryGetRaw(PdfNames.Metadata, out PdfValue entry))
                continue;

            if (context.Resolve(entry).AsStream is { } stream)
            {
                Add(stream);
            }
            else if (!ReferenceEquals(dictionary, context.Document.Catalog))
            {
                context.Report(
                    clause,
                    "A file-scope Metadata entry does not refer to a Metadata stream.");
            }
        }

        // A stream explicitly declaring itself as Metadata is still metadata when it is reached
        // through a producer-specific key rather than a standard Metadata entry.
        foreach (PdfStream stream in context.FileStreams)
        {
            if (stream.Dictionary.GetName(PdfNames.Type) == PdfNames.Metadata)
                Add(stream);
        }

        return result;

        void Add(PdfStream? stream)
        {
            if (stream is not null && seen.Add(stream))
                result.Add(stream);
        }
    }

    private static void CheckMetadataStreamDeclaration(
        PdfAContext context,
        PdfStream stream,
        string clause)
    {
        if (stream.Dictionary.GetName(PdfNames.Type) != PdfNames.Metadata)
        {
            context.Report(
                clause,
                "Every Metadata stream shall carry the exact /Type /Metadata declaration.");
        }

        if (stream.Dictionary.GetName(PdfNames.Subtype)?.Value != "XML")
        {
            context.Report(
                clause,
                "Every Metadata stream shall carry the exact /Subtype /XML declaration.");
        }
    }

    /// <summary>Clause 6.6.2.1: the two attributes the XMP specification deprecated.</summary>
    private static void CheckPacketHeader(PdfAContext context, string packet, string clause)
    {
        int header = packet.IndexOf("<?xpacket", StringComparison.Ordinal);
        if (header < 0)
            return;

        int close = packet.IndexOf("?>", header, StringComparison.Ordinal);
        string line = close < 0 ? packet[header..] : packet[header..close];

        foreach (string attribute in (string[])["bytes=", "encoding="])
        {
            if (line.Contains(attribute, StringComparison.Ordinal))
                context.Report(clause, $"The XMP packet header uses the deprecated {attribute.TrimEnd('=')} attribute.");
        }
    }

    private static void CheckPacketContents(PdfAContext context, XDocument document)
    {
        CheckIdentification(context, document);
        CheckExtensionSchemas(context, document);
    }

    /// <summary>
    /// Clause 6.7.11 of part 1, 6.6.4 of parts 2 and 3, 6.7.3 of part 4: the claim the file makes
    /// about itself.
    /// </summary>
    /// <remarks>
    /// The namespace URI is the one ISO approved as errata to part 4 (pdf-issues #123): the
    /// published text prints it with <c>https</c>, and the correction restores <c>http</c>, noting
    /// that a reader may accept either. The same errata fixes Table 2, which prints the level
    /// property as <c>pdfa:conformance</c> where it is in fact <c>pdfaid:conformance</c>.
    /// </remarks>
    private static void CheckIdentification(PdfAContext context, XDocument packet)
    {
        string clause = context.Clause("6.7.11", "6.6.4", "6.7.3");
        if (!PdfAXmpStructure.TryReadIdentification(
                packet,
                out PdfAXmpIdentification? identification,
                out string? structureProblem))
        {
            context.Report(clause, $"The PDF/A identification is invalid: {structureProblem}");
            return;
        }

        string? part = identification!.Part;
        string? level = identification.Conformance;

        if (part is null)
        {
            context.Report(
                clause,
                "The rdf:Description document subject does not carry pdfaid:part, so the file " +
                "never claims to be PDF/A.");
            return;
        }

        if (part != context.Part.ToString(System.Globalization.CultureInfo.InvariantCulture))
            context.Report(clause, $"The metadata claims part {part} while the document is being judged as part {context.Part}.");

        if (context.Part == 4)
        {
            CheckPartFourIdentification(context, identification.Revision, level, clause);
            return;
        }

        if (level is null)
            context.Report(clause, "The metadata does not carry pdfaid:conformance.");
        else if (!string.Equals(level, context.Level.ToString(), StringComparison.Ordinal))
            context.Report(clause, $"The metadata claims level {level} while the document is being judged as level {context.Level}.");
    }

    /// <summary>
    /// The part 4 form of the schema: <c>rev</c> becomes required and carries the four-digit year of
    /// the revision, while <c>conformance</c> turns optional and may only say <c>E</c> or <c>F</c>.
    /// A plain PDF/A-4 file, the standard states outright, shall provide no conformance property.
    /// </summary>
    private static void CheckPartFourIdentification(
        PdfAContext context, string? revision, string? level, string clause)
    {
        if (revision is null)
        {
            context.Report(
                clause,
                "The rdf:Description document subject does not carry pdfaid:rev, which part 4 requires.");
        }
        else if (revision.Length != 4 || !revision.All(char.IsAsciiDigit))
        {
            context.Report(
                clause,
                $"The metadata carries pdfaid:rev '{revision}'; it shall be the four digit year of the revision.");
        }
        else
        {
            string supportedRevision = PdfAProfile.SupportedPartFourRevision.ToString(
                System.Globalization.CultureInfo.InvariantCulture);
            if (!string.Equals(revision, supportedRevision, StringComparison.Ordinal))
            {
                context.Report(
                    clause,
                    $"The metadata carries unsupported pdfaid:rev '{revision}'; this validator implements " +
                    $"ISO 19005-4:{supportedRevision}.");
            }
        }

        if (context.Level == '\0')
        {
            if (level is not null)
            {
                context.Report(
                    clause,
                    $"The metadata claims conformance level {level}, but a file that is neither " +
                    "PDF/A-4e nor PDF/A-4f shall not provide one at all.");
            }

            return;
        }

        if (level is null)
            context.Report(clause, $"The metadata does not carry pdfaid:conformance, which {context.Conformance.Name} requires.");
        else if (!string.Equals(level, context.Level.ToString(), StringComparison.Ordinal))
            context.Report(clause, $"The metadata claims level {level} while the document is being judged as level {context.Level}.");
    }

    /// <summary>
    /// Clause 6.7.8 of part 1, 6.6.2.3.2 of parts 2 and 3. Part 4 replaced this normative rule with
    /// a different recommendation. A property in a namespace the earlier standards
    /// does not know is allowed, but only if the packet explains the namespace: otherwise the file
    /// carries information that nothing left in the world can interpret.
    /// </summary>
    private static void CheckExtensionSchemas(PdfAContext context, XDocument packet)
    {
        // Part 4 replaced the old mandatory in-packet extension-schema description with a
        // recommendation to associate an ISO 16684-2 schema file. The old rule is not normative.
        if (context.Part == 4)
            return;

        string clause = context.Clause("6.7.8", "6.6.2.3.2");
        XNamespace schema = "http://www.aiim.org/pdfa/ns/schema#";

        XNamespace rdfNamespace = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
        HashSet<string> described = new(StringComparer.Ordinal);

        foreach (XElement element in packet.Descendants(schema + "namespaceURI"))
        {
            // A URI-valued property may be written as text or, properly, as an rdf:resource.
            described.Add(element.Value.Trim());

            if (element.Attribute(rdfNamespace + "resource")?.Value is { } resource)
                described.Add(resource.Trim());
        }

        // RDF also lets a simple property be written as an attribute instead of an element, and
        // plenty of producers do exactly that for the extension schema fields.
        foreach (XElement element in packet.Descendants())
        {
            if (element.Attribute(schema + "namespaceURI")?.Value is { } uri)
                described.Add(uri.Trim());
        }

        foreach (string used in UsedNamespaces(packet))
        {
            if (_knownNamespaces.Contains(used, StringComparer.Ordinal) || described.Contains(used))
                continue;

            context.Report(clause, $"The metadata uses the schema {used} without describing it as an extension schema.");
        }
    }

    private static HashSet<string> UsedNamespaces(XDocument packet)
    {
        XNamespace rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
        HashSet<string> used = new(StringComparer.Ordinal);

        foreach (XElement description in packet.Descendants(rdf + "Description"))
        {
            foreach (XElement property in description.Elements())
                used.Add(property.Name.NamespaceName);
        }

        return used;
    }

    /// <summary>
    /// Clause 6.1.5 with 6.7.3 of part 1: the information dictionary and the packet have to tell the
    /// same story. Parts 2 and 3 only recommend it, so a disagreement is reported for part 1 alone.
    /// </summary>
    private static void CheckDocumentInformation(PdfAContext context, string packet)
    {
        if (context.Part != 1)
            return;

        Compare(context, packet, "Title", context.Document.Info.Title);
        Compare(context, packet, "Author", context.Document.Info.Author);
        Compare(context, packet, "Keywords", context.Document.Info.Keywords);
        Compare(context, packet, "Producer", context.Document.Info.Producer);

        CompareDate(context, packet, "CreationDate", "CreateDate", context.Document.Info.CreationDate);
        CompareDate(context, packet, "ModDate", "ModifyDate", context.Document.Info.ModificationDate);
    }

    /// <summary>
    /// The two sides write dates in different notations — D:20260726120000+03'00' against ISO 8601 —
    /// so equivalence means the same instant, not the same characters. A date the packet does not
    /// carry at all is reported too: 6.7.3 requires the analogous property, not merely a compatible
    /// one. Packets in the wild write the XMP basic schema under xmp: or the older xap:, as either
    /// an element or an attribute, and all four spellings say the same thing.
    /// </summary>
    private static void CompareDate(
        PdfAContext context, string packet, string entry, string property, DateTimeOffset? value)
    {
        if (value is null)
            return;

        string? written = FindProperty(packet, $"xmp:{property}") ?? FindProperty(packet, $"xap:{property}");
        if (written is null)
        {
            context.Report(
                "6.7.3",
                $"The information dictionary carries {entry}, and the XMP packet has no xmp:{property} to match it.");

            return;
        }

        if (!DateTimeOffset.TryParse(written, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset parsed))
            return;

        if (Math.Abs((parsed.UtcDateTime - value.Value.UtcDateTime).TotalSeconds) >= 1)
        {
            context.Report(
                "6.7.3",
                $"The information dictionary puts {entry} at {value:O} and the XMP packet at {parsed:O}; " +
                "part 1 wants the two to agree.");
        }
    }

    /// <summary>Reads one simple XMP property, written as an element or as an attribute.</summary>
    private static string? FindProperty(string packet, string name)
    {
        int start = packet.IndexOf($"<{name}>", StringComparison.Ordinal);
        if (start >= 0)
        {
            int end = packet.IndexOf($"</{name}>", start, StringComparison.Ordinal);
            if (end >= 0)
                return packet[(start + name.Length + 2)..end].Trim();
        }

        int attribute = packet.IndexOf($"{name}=\"", StringComparison.Ordinal);
        if (attribute >= 0)
        {
            int open = attribute + name.Length + 2;
            int close = packet.IndexOf('"', open);
            if (close >= 0)
                return packet[open..close].Trim();
        }

        return null;
    }

    private static void Compare(PdfAContext context, string packet, string entry, string? value)
    {
        if (string.IsNullOrEmpty(value) || packet.Contains(System.Security.SecurityElement.Escape(value)!, StringComparison.Ordinal))
            return;

        context.Report(
            "6.7.3",
            $"The information dictionary says {entry} is '{value}', and the XMP packet does not say so.");
    }

}
