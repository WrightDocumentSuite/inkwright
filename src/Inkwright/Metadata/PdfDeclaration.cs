using System.Globalization;
using System.Text;
using System.Xml.Linq;
using Inkwright.Cos;

namespace Inkwright.Metadata;

/// <summary>
/// A claim that a document conforms to some standard outside PDF itself — an accessibility profile,
/// a privacy regulation, an engineering tolerance — recorded in the metadata where a machine can
/// read it. The PDF Association's PDF Declarations specification defines the form.
/// </summary>
/// <param name="ConformsTo">
/// The identifier of the standard being claimed, which by convention is a URI so that two producers
/// naming the same standard say the same thing.
/// </param>
public sealed record PdfDeclaration(string ConformsTo)
{
    /// <summary>Who made the claim, when one wishes to be named.</summary>
    public string? ClaimedBy { get; init; }

    /// <summary>When the claim was made.</summary>
    public DateTimeOffset? ClaimedOn { get; init; }

    /// <summary>What authority the claimant holds for making it.</summary>
    public string? Credentials { get; init; }

    /// <summary>Where a report backing the claim can be read.</summary>
    public string? Report { get; init; }

    /// <inheritdoc />
    public override string ToString() => ConformsTo;
}

/// <summary>
/// Reads and writes the conformance claims a document carries. A declaration asserts something the
/// file itself cannot prove: its presence is a statement by the author, not evidence, and the
/// specification says so outright.
/// </summary>
public static class PdfDeclarations
{
    /// <summary>The namespace the schema uses.</summary>
    public const string Namespace = "http://pdfa.org/declarations/";

    private static readonly XNamespace Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    private static readonly XNamespace Pdfd = Namespace;

    /// <summary>Reads the claims a document makes.</summary>
    /// <param name="document">The document to read.</param>
    public static IReadOnlyList<PdfDeclaration> Read(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (document.Resolve(document.Catalog.Get(PdfName.Get("Metadata"))).AsStream is not { } stream)
            return [];

        string xmp;
        try
        {
            xmp = Encoding.UTF8.GetString(stream.Decode().Span);
        }
        catch (Exception exception) when (exception is InvalidDataException or NotSupportedException)
        {
            return [];
        }

        return Parse(xmp);
    }

    /// <summary>Reads the claims out of an XMP packet.</summary>
    /// <param name="xmp">The packet.</param>
    public static IReadOnlyList<PdfDeclaration> Parse(string xmp)
    {
        ArgumentNullException.ThrowIfNull(xmp);

        XDocument parsed;
        try
        {
            parsed = XDocument.Parse(xmp, LoadOptions.None);
        }
        catch (System.Xml.XmlException)
        {
            return [];
        }

        var claims = new List<PdfDeclaration>();

        foreach (XElement item in parsed.Descendants(Pdfd + "declarations")
                     .Elements(Rdf + "Bag")
                     .Elements(Rdf + "li"))
        {
            string? conformsTo = item.Element(Pdfd + "conformsTo")?.Value.Trim();
            if (string.IsNullOrEmpty(conformsTo))
                continue;

            XElement? data = item.Element(Pdfd + "claimData")
                ?.Element(Rdf + "Bag")
                ?.Element(Rdf + "li");

            claims.Add(new PdfDeclaration(conformsTo)
            {
                ClaimedBy = Trimmed(data?.Element(Pdfd + "claimBy")?.Value),
                ClaimedOn = ReadDate(data?.Element(Pdfd + "claimDate")?.Value),
                Credentials = Trimmed(data?.Element(Pdfd + "claimCredentials")?.Value),
                Report = Trimmed(data?.Element(Pdfd + "claimReport")?.Value),
            });
        }

        return claims;
    }

    /// <summary>Builds the metadata block for a set of claims.</summary>
    /// <param name="declarations">The claims to record.</param>
    /// <returns>An <c>rdf:Description</c> element ready to add to a packet.</returns>
    public static string Build(IEnumerable<PdfDeclaration> declarations)
    {
        ArgumentNullException.ThrowIfNull(declarations);

        var builder = new StringBuilder();
        builder.Append(CultureInfo.InvariantCulture, $"""<rdf:Description rdf:about="" xmlns:pdfd="{Namespace}">""");
        builder.Append("\n      <pdfd:declarations>\n        <rdf:Bag>\n");

        foreach (PdfDeclaration declaration in declarations)
        {
            builder.Append("""          <rdf:li rdf:parseType="Resource">""").Append('\n');
            builder.Append(CultureInfo.InvariantCulture,
                $"            <pdfd:conformsTo>{Escape(declaration.ConformsTo)}</pdfd:conformsTo>\n");

            if (HasClaimData(declaration))
            {
                builder.Append("            <pdfd:claimData>\n              <rdf:Bag>\n");
                builder.Append("""                <rdf:li rdf:parseType="Resource">""").Append('\n');

                Append(builder, "claimBy", declaration.ClaimedBy);
                Append(builder, "claimDate", declaration.ClaimedOn?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                Append(builder, "claimCredentials", declaration.Credentials);
                Append(builder, "claimReport", declaration.Report);

                builder.Append("                </rdf:li>\n              </rdf:Bag>\n            </pdfd:claimData>\n");
            }

            builder.Append("          </rdf:li>\n");
        }

        builder.Append("        </rdf:Bag>\n      </pdfd:declarations>\n    </rdf:Description>");
        return builder.ToString();
    }

    private static bool HasClaimData(PdfDeclaration declaration) =>
        declaration.ClaimedBy is not null || declaration.ClaimedOn is not null ||
        declaration.Credentials is not null || declaration.Report is not null;

    private static void Append(StringBuilder builder, string field, string? value)
    {
        if (string.IsNullOrEmpty(value))
            return;

        builder.Append(CultureInfo.InvariantCulture,
            $"                  <pdfd:{field}>{Escape(value)}</pdfd:{field}>\n");
    }

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static DateTimeOffset? ReadDate(string? value) =>
        DateTimeOffset.TryParse(
            Trimmed(value), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out DateTimeOffset parsed)
            ? parsed
            : null;

    private static string Escape(string value) => new XText(value).ToString();
}
