using System.Text;
using System.Globalization;
using System.Xml.Linq;
using Inkwright.Cos;

namespace Inkwright.Layout;

/// <summary>
/// The document-wide rules of ISO 14289: identification, tagging, metadata, language and security.
/// </summary>
internal static partial class PdfUaRules
{
    /// <summary>
    /// Clause 5: a conforming file says so in its metadata, through the identification schema whose
    /// namespace is the one named here.
    /// </summary>
    private const string IdentificationNamespace = "http://www.aiim.org/pdfua/ns/id/";

    /// <summary>Checks that the file identifies itself as conforming (clause 5).</summary>
    internal static void CheckIdentification(PdfUaContext context)
    {
        string? metadata = ReadMetadata(context);

        if (metadata is null)
        {
            context.Violation("5", "The catalogue carries no XMP metadata, so the file cannot identify itself.");
            return;
        }

        XDocument packet;
        try
        {
            packet = XDocument.Parse(metadata, LoadOptions.PreserveWhitespace);
        }
        catch (System.Xml.XmlException exception)
        {
            context.Violation("5", $"The XMP metadata is not well-formed XML: {exception.Message}");
            return;
        }

        XNamespace identification = IdentificationNamespace;
        List<string> parts = Values(packet, identification + "part");
        if (parts.Count != 1 ||
            !int.TryParse(parts.SingleOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int part) ||
            part != context.Part)
        {
            context.Violation(
                "5",
                $"The PDF/UA identification property part shall occur once with the value {context.Part}.");
        }

        if (context.Part == 2)
        {
            List<string> revisions = Values(packet, identification + "rev");
            string? revision = revisions.Count == 1 ? revisions[0] : null;
            if (revisions.Count != 1 || revision is null || revision.Length != 4 ||
                !revision.All(char.IsAsciiDigit) || revision != "2024")
            {
                context.Violation(
                    "5",
                    "PDF/UA-2 requires exactly one pdfuaid:rev property with the four-digit value 2024.");
            }
        }
    }

    private static List<string> Values(XDocument packet, XName property)
    {
        var values = packet.Descendants(property).Select(element => element.Value.Trim()).ToList();
        values.AddRange(packet.Descendants().Attributes(property).Select(attribute => attribute.Value.Trim()));
        return values;
    }

    /// <summary>
    /// Checks that the content is tagged at all, and that the file does not admit to being unsure
    /// about it (part 1 clause 7.1, part 2 clause 8.2).
    /// </summary>
    internal static void CheckTagging(PdfUaContext context)
    {
        string clause = context.Clause("7.1", "8.2");

        if (context.Resolve(context.Catalog.Get(PdfName.Get("StructTreeRoot"))).AsDictionary is null)
        {
            context.Violation(clause, "There is no structure tree, so no content is tagged.");
            return;
        }

        PdfDictionary? markInfo = context.Resolve(context.Catalog.Get(PdfName.Get("MarkInfo"))).AsDictionary;

        if (markInfo is null || !markInfo.Get(PdfName.Get("Marked")).AsBoolean)
        {
            context.Violation(
                clause,
                "The mark information dictionary does not say the document is marked.");
        }

        // A file that flags its own tagging as suspect cannot claim conformance.
        if (markInfo is not null && markInfo.Get(PdfName.Get("Suspects")).AsBoolean)
            context.Violation(clause, "The mark information dictionary flags the tagging as suspect.");

        if (context.Structure.Count == 0)
            context.Violation(clause, "The structure tree is empty.");
    }

    /// <summary>
    /// Checks the role map: a tag the standard does not define has to lead, eventually, to one it
    /// does, and a tag the standard does define must not be given a different meaning
    /// (part 1 clause 7.1).
    /// </summary>
    internal static void CheckRoleMap(PdfUaContext context)
    {
        string clause = context.Clause("7.1", "8.2.2");

        if (context.Resolve(context.Catalog.Get(PdfName.Get("StructTreeRoot"))).AsDictionary is not { } root)
            return;

        if (context.Resolve(root.Get(PdfName.Get("RoleMap"))).AsDictionary is not { } roleMap)
            return;

        foreach (PdfName key in roleMap.Keys.ToArray())
        {
            if (StandardTypes.Contains(key.Value))
            {
                context.Violation(
                    clause,
                    $"The role map remaps the standard type {key.Value}, which the standard forbids.");
            }

            if (!Terminates(context, roleMap, key, 0))
            {
                context.Violation(
                    clause,
                    $"The role map entry for {key.Value} never reaches a standard structure type.");
            }
        }
    }

    private static bool Terminates(PdfUaContext context, PdfDictionary roleMap, PdfName key, int depth)
    {
        if (depth > 16)
            return false;

        if (context.Resolve(roleMap.Get(key)).AsName is not { } target)
            return false;

        return StandardTypes.Contains(target.Value) || Terminates(context, roleMap, target, depth + 1);
    }

    /// <summary>
    /// Checks the metadata and viewer preferences: the title has to be there, and the reader has to
    /// be told to show it rather than the file name (part 1 clause 7.1, part 2 clause 8.11).
    /// </summary>
    internal static void CheckMetadata(PdfUaContext context)
    {
        string clause = context.Clause("7.1", "8.11.1");
        string? metadata = ReadMetadata(context);

        if (metadata is not null && !metadata.Contains("dc:title", StringComparison.Ordinal))
            context.Violation(clause, "The metadata carries no title, so nothing identifies the document.");

        string preferencesClause = context.Clause("7.1", "8.11.2");
        PdfDictionary? preferences =
            context.Resolve(context.Catalog.Get(PdfName.Get("ViewerPreferences"))).AsDictionary;

        if (preferences is null)
        {
            context.Violation(
                preferencesClause,
                "There is no viewer preferences dictionary, so the title cannot be shown in place of the file name.");
            return;
        }

        if (!preferences.Get(PdfName.Get("DisplayDocTitle")).AsBoolean)
        {
            context.Violation(
                preferencesClause,
                "The viewer preferences do not ask for the document title to be displayed.");
        }
    }

    /// <summary>
    /// Checks that the document declares what language it is in (part 1 clause 7.2, part 2 clause
    /// 8.4.5). Without it a screen reader has to guess, and it guesses by pronouncing the text in
    /// whatever language it was set up for.
    /// </summary>
    internal static void CheckLanguage(PdfUaContext context)
    {
        string clause = context.Clause("7.2", "8.4.5");
        string? language = context.Catalog.GetTextString(PdfName.Get("Lang"));

        if (!string.IsNullOrWhiteSpace(language))
            return;

        // A document may instead declare the language on every structure element, though almost
        // none do; the catalogue entry is only missing legitimately if the tree covers it.
        bool declared = context.Structure.Count > 0 &&
                        context.Structure.All(node => !string.IsNullOrWhiteSpace(node.Language));

        if (!declared)
            context.Violation(clause, "The document does not declare a natural language.");
    }

    /// <summary>
    /// Checks that encryption still lets assistive technology read the file (part 1 clause 7.16).
    /// The tenth bit of the permissions is the one that grants it.
    /// </summary>
    internal static void CheckSecurity(PdfUaContext context)
    {
        if (!context.Document.WasEncrypted)
            return;

        string clause = context.Clause("7.16", "8.1");

        if (context.Resolve(context.Document.Trailer.Get(PdfName.Get("Encrypt"))).AsDictionary is not { } encrypt)
        {
            context.Violation(clause, "The document is encrypted but carries no encryption dictionary.");
            return;
        }

        PdfValue permissions = encrypt.Get(PdfName.Get("P"));
        if (permissions.IsNull)
        {
            context.Violation(clause, "The encryption dictionary states no permissions.");
            return;
        }

        // Bit positions are numbered from one, so the tenth bit is the value 512.
        if ((permissions.AsInteger & 512) == 0)
        {
            context.Violation(
                clause,
                "The permissions withhold the bit that lets assistive technology extract content.");
        }
    }

    /// <summary>
    /// Checks the optional content configurations (part 1 clause 7.10, part 2 clause 8.7). A layer
    /// a reader can turn on has to have a name a person can read, and a configuration must not
    /// switch itself based on how the document is being used.
    /// </summary>
    internal static void CheckOptionalContent(PdfUaContext context)
    {
        string clause = context.Clause("7.10", "8.7");

        if (context.Resolve(context.Catalog.Get(PdfName.Get("OCProperties"))).AsDictionary is not { } properties)
            return;

        PdfArray? alternatives = context.Resolve(properties.Get(PdfName.Get("Configs"))).AsArray;

        foreach (PdfDictionary configuration in OptionalContentConfigurations(context))
        {
            // The name is only required once the file offers a choice of configurations.
            if (alternatives is { Count: > 0 } &&
                string.IsNullOrWhiteSpace(configuration.GetTextString(PdfName.Get("Name"))))
            {
                context.Violation(clause, "An optional content configuration has no name.");
            }

            if (!configuration.Get(PdfName.Get("AS")).IsNull)
            {
                context.Violation(
                    clause,
                    "An optional content configuration carries an automatic state entry, which the standard forbids.");
            }
        }
    }

    private static string? ReadMetadata(PdfUaContext context)
    {
        if (context.Resolve(context.Catalog.Get(PdfName.Get("Metadata"))).AsStream is not { } stream)
            return null;

        try
        {
            return Encoding.UTF8.GetString(stream.Decode().Span);
        }
        catch (Exception exception) when (exception is InvalidDataException or NotSupportedException)
        {
            return null;
        }
    }
}
