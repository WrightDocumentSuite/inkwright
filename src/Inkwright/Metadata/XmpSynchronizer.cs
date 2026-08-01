using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Inkwright.Cos;

namespace Inkwright.Metadata;

/// <summary>
/// Keeps the XMP packet honest when a save rewrites the information dictionary. Stamping a new
/// ModDate or Producer into DocInfo while the packet still swears by the old ones is exactly the
/// disagreement ISO 19005-1, 6.7.3 forbids, and it would creep in on every save of an archival
/// file. The packet is patched in place — only properties it already carries are touched, so a
/// foreign packet is never restructured, merely kept truthful.
/// </summary>
internal static partial class XmpSynchronizer
{
    [GeneratedRegex("""(<pdf:Producer>)[^<]*(</pdf:Producer>)|(pdf:Producer=")[^"]*(")""")]
    private static partial Regex Producer();

    [GeneratedRegex("""(<xmp:ModifyDate>)[^<]*(</xmp:ModifyDate>)|(xmp:ModifyDate=")[^"]*(")""")]
    private static partial Regex ModifyDate();

    [GeneratedRegex("""(<xmp:MetadataDate>)[^<]*(</xmp:MetadataDate>)|(xmp:MetadataDate=")[^"]*(")""")]
    private static partial Regex MetadataDate();

    internal static void Sync(PdfDocument document, PdfSaveOptions options)
    {
        if (!options.SetProducer && !options.UpdateModificationDate)
            return;

        PdfValue reference = document.Catalog.Get(PdfName.Get("Metadata"));
        if (document.Resolve(reference).AsStream is not { } stream)
            return;

        string packet;
        try
        {
            packet = Encoding.UTF8.GetString(stream.DecodedSpan());
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            return;
        }

        if (!packet.Contains("<x:xmpmeta", StringComparison.Ordinal))
            return;

        string patched = packet;

        if (options.SetProducer)
            patched = Replace(Producer(), patched, PdfDocument.ProducerName);

        if (options.UpdateModificationDate && document.Info.ModificationDate is { } moment)
        {
            string date = moment.ToString("yyyy-MM-ddTHH:mm:ssK", CultureInfo.InvariantCulture);
            patched = Replace(ModifyDate(), patched, date);
            patched = Replace(MetadataDate(), patched, date);
        }

        if (ReferenceEquals(patched, packet) || patched == packet)
            return;

        // The packet is patched in place rather than reattached as a fresh object: a new object
        // would leave the old packet in the file as an orphan still telling the old story.
        stream.SetData(Encoding.UTF8.GetBytes(patched), compress: false);

        if (reference.IsReference)
            document.MarkChanged(reference.AsReference);
    }

    /// <summary>Swaps the property value whether the packet wrote it as an element or an attribute.</summary>
    private static string Replace(Regex property, string packet, string value)
    {
        string escaped = System.Security.SecurityElement.Escape(value) ?? string.Empty;
        return property.Replace(packet, match => $"{match.Groups[1]}{match.Groups[3]}{escaped}{match.Groups[2]}{match.Groups[4]}");
    }
}
