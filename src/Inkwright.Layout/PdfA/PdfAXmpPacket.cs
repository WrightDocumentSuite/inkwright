using System.Runtime.InteropServices;
using System.Xml;
using System.Xml.Linq;
using Inkwright.Cos;
using Inkwright.Diagnostics;
using Inkwright.Filters;

namespace Inkwright.Layout;

/// <summary>A strictly decoded, bounded XMP packet shared by claim and metadata checks.</summary>
internal sealed record PdfAXmpPacket(string Text, XDocument Document);

internal sealed record PdfAXmpIdentification(string? Part, string? Conformance, string? Revision);

/// <summary>Recognises the RDF envelope and the single document subject used by PDF/A identification.</summary>
internal static class PdfAXmpStructure
{
    private static readonly XNamespace Xmp = "adobe:ns:meta/";
    private static readonly XNamespace Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    private static readonly XNamespace PdfaId = "http://www.aiim.org/pdfa/ns/id/";

    internal static bool TryGetDescriptions(
        XDocument packet,
        out IReadOnlyList<XElement> descriptions,
        out string? problem)
    {
        XElement? root = packet.Root;
        XElement? rdf = null;
        if (root?.Name == Rdf + "RDF")
        {
            rdf = root;
        }
        else if (root?.Name == Xmp + "xmpmeta")
        {
            XElement[] candidates = root.Elements(Rdf + "RDF").ToArray();
            if (candidates.Length == 1)
                rdf = candidates[0];
        }

        if (rdf is null)
        {
            descriptions = [];
            problem =
                "the XML document is not an rdf:RDF packet or an optional x:xmpmeta wrapper " +
                "containing exactly one rdf:RDF element.";
            return false;
        }

        XElement[] found = rdf.Elements(Rdf + "Description").ToArray();
        if (found.Length == 0)
        {
            descriptions = [];
            problem = "the rdf:RDF element contains no rdf:Description.";
            return false;
        }

        descriptions = found;
        problem = null;
        return true;
    }

    internal static bool TryReadIdentification(
        XDocument packet,
        out PdfAXmpIdentification? identification,
        out string? problem)
    {
        identification = null;
        if (!TryGetDescriptions(packet, out IReadOnlyList<XElement> descriptions, out problem))
            return false;

        // Only an explicit empty rdf:about denotes the host PDF. A missing attribute is an RDF
        // blank node, while a non-empty value (including one resolved through XML Base) denotes a
        // different subject; neither may lend identification properties to the document claim.
        XElement[] documentSubject = descriptions
            .Where(IsDocumentSubject)
            .ToArray();
        if (documentSubject.Length == 0)
        {
            problem =
                "the rdf:RDF packet has no rdf:Description document subject with rdf:about=\"\".";
            return false;
        }

        if (!TryReadUnique(documentSubject, PdfaId + "part", out string? part))
        {
            problem = "the rdf:Description document subject has conflicting pdfaid:part values.";
            return false;
        }

        if (!TryReadUnique(documentSubject, PdfaId + "conformance", out string? conformance))
        {
            problem =
                "the rdf:Description document subject has conflicting pdfaid:conformance values.";
            return false;
        }

        if (!TryReadUnique(documentSubject, PdfaId + "rev", out string? revision))
        {
            problem = "the rdf:Description document subject has conflicting pdfaid:rev values.";
            return false;
        }

        identification = new PdfAXmpIdentification(part, conformance, revision);
        problem = null;
        return true;
    }

    private static bool IsDocumentSubject(XElement description) =>
        description.Attribute(Rdf + "about")?.Value.Trim().Length == 0 &&
        !description.AncestorsAndSelf()
            .Any(element => element.Attribute(XNamespace.Xml + "base") is not null);

    private static bool TryReadUnique(
        IReadOnlyList<XElement> descriptions,
        XName property,
        out string? value)
    {
        HashSet<string> values = new(StringComparer.Ordinal);
        for (int i = 0; i < descriptions.Count; i++)
        {
            XElement description = descriptions[i];
            foreach (XElement element in description.Elements(property))
                values.Add(element.Value.Trim());

            if (description.Attribute(property)?.Value is { } attribute)
                values.Add(attribute.Trim());
        }

        if (values.Count > 1)
        {
            value = null;
            return false;
        }

        value = values.FirstOrDefault();
        return true;
    }
}

internal sealed partial class PdfAValidationSession
{
    private readonly Dictionary<PdfStream, PdfAXmpReadResult> _xmpPackets =
        new(ReferenceEqualityComparer.Instance);
    private long _xmpBytes;
    private long _xmpCharacters;

    /// <summary>
    /// Decodes and parses a document's XMP once. Both byte and character charges are monotonic and
    /// shared by the root document and every recursively opened child.
    /// </summary>
    internal bool TryReadXmp(
        PdfDocument document,
        out PdfAXmpPacket? packet,
        out string? problem)
    {
        PdfStream? metadata = document.Catalog.Get(PdfNames.Metadata).AsStream;
        if (metadata is null)
        {
            packet = null;
            problem = "the document catalog has no Metadata stream.";
            return false;
        }

        return TryReadXmp(metadata, out packet, out problem);
    }

    /// <summary>Reads one reachable Metadata stream once under the validation tree's shared limits.</summary>
    internal bool TryReadXmp(
        PdfStream metadata,
        out PdfAXmpPacket? packet,
        out string? problem)
    {
        if (_xmpPackets.TryGetValue(metadata, out PdfAXmpReadResult cached))
            return cached.CopyTo(out packet, out problem);

        long remainingBytes = _limits.MaxTotalXmpBytes - _xmpBytes;
        if (remainingBytes <= 0)
        {
            return CacheFailure(
                metadata,
                $"the shared XMP byte limit of {_limits.MaxTotalXmpBytes} bytes was exceeded.",
                out packet,
                out problem);
        }

        int probeLimit = remainingBytes >= PdfFilterLimits.Cap
            ? PdfFilterLimits.Cap
            : checked((int)remainingBytes + 1);
        PdfBoundedDecodeResult decoded;
        try
        {
            decoded = metadata.DecodeBoundedWithPolicy(probeLimit);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or PdfFormatException)
        {
            SaturateXmpBytes();
            return CacheFailure(
                metadata,
                $"the Metadata stream cannot be decoded ({exception.Message}).",
                out packet,
                out problem);
        }

        if (decoded.Data.Length > remainingBytes)
        {
            SaturateXmpBytes();
            return CacheFailure(
                metadata,
                $"the shared XMP byte limit of {_limits.MaxTotalXmpBytes} bytes was exceeded.",
                out packet,
                out problem);
        }

        // Failed bounded decodes still materialize a known prefix (or an undecoded terminal
        // payload). Charge that known amount before judging the status so different malformed
        // siblings cannot repeatedly consume the same nominal XMP allowance for free.
        ChargeXmpBytes(decoded.Data.Length);

        if (decoded.Status != PdfBoundedDecodeStatus.Complete)
        {
            return CacheFailure(
                metadata,
                $"the Metadata stream cannot be decoded completely by the bounded validator " +
                $"({decoded.Status}).",
                out packet,
                out problem);
        }

        ReadOnlyMemory<byte> bytes = decoded.Data;

        long availableCharacters = _limits.MaxTotalXmpCharacters - _xmpCharacters;
        if (availableCharacters <= 0)
        {
            return CacheFailure(
                metadata,
                $"the shared XMP character limit of {_limits.MaxTotalXmpCharacters} characters was exceeded.",
                out packet,
                out problem);
        }

        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                MaxCharactersInDocument = availableCharacters,
                XmlResolver = null,
            };
            using MemoryStream input = OpenReadOnly(bytes);
            using XmlReader reader = XmlReader.Create(input, settings);
            XDocument documentPacket = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
            string text = documentPacket.ToString(SaveOptions.DisableFormatting);
            if (text.Length > availableCharacters)
            {
                SaturateXmpCharacters();
                return CacheFailure(
                    metadata,
                    $"the shared XMP character limit of {_limits.MaxTotalXmpCharacters} characters was exceeded.",
                    out packet,
                    out problem);
            }

            _xmpCharacters += text.Length;
            var parsed = new PdfAXmpPacket(text, documentPacket);
            _xmpPackets.Add(metadata, new PdfAXmpReadResult(parsed, null));
            packet = parsed;
            problem = null;
            return true;
        }
        catch (XmlException exception) when (
            exception.Message.Contains("MaxCharactersInDocument", StringComparison.Ordinal))
        {
            SaturateXmpCharacters();
            return CacheFailure(
                metadata,
                $"the shared XMP character limit of {_limits.MaxTotalXmpCharacters} characters was exceeded.",
                out packet,
                out problem);
        }
        catch (XmlException exception)
        {
            // XmlReader does not expose how many characters it consumed before rejecting a
            // packet. Close the shared character ledger so siblings cannot each force another
            // near-limit parse through a different malformed document.
            SaturateXmpCharacters();
            return CacheFailure(
                metadata,
                $"the Metadata stream is not well-formed XML or uses an invalid encoding ({exception.Message}).",
                out packet,
                out problem);
        }
    }

    private static MemoryStream OpenReadOnly(ReadOnlyMemory<byte> bytes)
    {
        if (MemoryMarshal.TryGetArray(bytes, out ArraySegment<byte> segment) &&
            segment.Array is { } array)
        {
            return new MemoryStream(
                array,
                segment.Offset,
                segment.Count,
                writable: false,
                publiclyVisible: true);
        }

        return new MemoryStream(bytes.ToArray(), writable: false);
    }

    private void ChargeXmpBytes(int count)
    {
        if (count >= _limits.MaxTotalXmpBytes - _xmpBytes)
        {
            SaturateXmpBytes();
            return;
        }

        _xmpBytes += count;
    }

    private void SaturateXmpBytes() => _xmpBytes = _limits.MaxTotalXmpBytes;

    private void SaturateXmpCharacters() => _xmpCharacters = _limits.MaxTotalXmpCharacters;

    private bool CacheFailure(
        PdfStream metadata,
        string failure,
        out PdfAXmpPacket? packet,
        out string? problem)
    {
        _xmpPackets.Add(metadata, new PdfAXmpReadResult(null, failure));
        packet = null;
        problem = failure;
        return false;
    }

    private readonly record struct PdfAXmpReadResult(PdfAXmpPacket? Packet, string? Problem)
    {
        internal bool CopyTo(out PdfAXmpPacket? packet, out string? problem)
        {
            packet = Packet;
            problem = Problem;
            return packet is not null;
        }
    }
}
