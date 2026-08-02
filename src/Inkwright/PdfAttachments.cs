using System.Security.Cryptography;
using System.Text;
using Inkwright.Cos;

namespace Inkwright;

/// <summary>
/// How an attached file relates to the document it travels with (ISO 32000-2 §14.13). Hybrid
/// invoices depend on this: the machine-readable XML is the <see cref="Data"/> counterpart of the
/// human-readable page, and a consumer picks whichever it can use.
/// </summary>
public enum AttachmentRelationship
{
    /// <summary>The relationship is not stated.</summary>
    Unspecified,

    /// <summary>The attachment is the source the document was rendered from.</summary>
    Source,

    /// <summary>The attachment is the machine-readable form of what the pages show.</summary>
    Data,

    /// <summary>The attachment is an alternative representation of the whole document.</summary>
    Alternative,

    /// <summary>The attachment adds material the document refers to.</summary>
    Supplement,
}

/// <summary>Files carried inside a document.</summary>
public sealed class PdfAttachments
{
    private readonly PdfDocument _document;

    internal PdfAttachments(PdfDocument document) => _document = document;

    /// <summary>The names of the files currently attached.</summary>
    public IReadOnlyList<string> Names
    {
        get
        {
            List<string> names = [];
            if (EmbeddedFilesNameTree(create: false) is not { } tree)
                return names;

            if (tree.GetArray(PdfName.Get("Names")) is not { } entries)
                return names;

            for (int i = 0; i + 1 < entries.Count; i += 2)
            {
                if (entries.Get(i).AsTextString is { } name)
                    names.Add(name);
            }

            return names;
        }
    }

    /// <summary>Reads an attachment back out.</summary>
    /// <param name="name">The name the file was attached under.</param>
    /// <returns>The file contents, or <see langword="null"/> when there is no such attachment.</returns>
    public byte[]? Read(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        if (EmbeddedFilesNameTree(create: false)?.GetArray(PdfName.Get("Names")) is not { } entries)
            return null;

        for (int i = 0; i + 1 < entries.Count; i += 2)
        {
            if (!string.Equals(entries.Get(i).AsTextString, name, StringComparison.Ordinal))
                continue;

            PdfDictionary? spec = entries.Get(i + 1).AsDictionary;
            return spec?.GetDictionary(PdfNames.EF)?.GetStream(PdfNames.F)?.Decode().ToArray();
        }

        return null;
    }

    /// <summary>Attaches a file.</summary>
    /// <param name="name">The name the file is listed under.</param>
    /// <param name="content">The file contents.</param>
    /// <param name="mimeType">The media type, such as <c>text/xml</c>.</param>
    /// <param name="description">A human-readable description.</param>
    /// <param name="relationship">How the file relates to the document.</param>
    /// <returns>A reference to the file specification, for callers that need to point at it.</returns>
    public PdfValue Add(
        string name,
        ReadOnlyMemory<byte> content,
        string mimeType = "application/octet-stream",
        string? description = null,
        AttachmentRelationship relationship = AttachmentRelationship.Unspecified)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        var stream = PdfStream.FromData(content);
        PdfDictionary streamDictionary = stream.Dictionary;
        streamDictionary.Set(PdfNames.Type, PdfValue.Name(PdfNames.EmbeddedFile));
        streamDictionary.Set(PdfNames.Subtype, PdfValue.Name(PdfName.Get(mimeType)));

        var parameters = new PdfDictionary(3);
        parameters.Set(PdfNames.Size, PdfValue.Integer(content.Length));
        parameters.Set(PdfNames.ModDate, PdfValue.TextString(PdfDate.Format(DateTimeOffset.Now)));
        parameters.Set(PdfNames.CheckSum, PdfValue.String(MD5.HashData(content.Span), hex: true));
        streamDictionary.Set(PdfNames.Params, PdfValue.Dictionary(parameters));

        PdfObjectId streamId = _document.AddObject(PdfValue.Stream(stream));

        var files = new PdfDictionary(1);
        files.Set(PdfNames.F, PdfValue.Reference(streamId));
        files.Set(PdfName.Get("UF"), PdfValue.Reference(streamId));

        var spec = new PdfDictionary(6);
        spec.Set(PdfNames.Type, PdfValue.Name(PdfNames.Filespec));
        spec.Set(PdfNames.F, PdfValue.TextString(name));
        spec.Set(PdfNames.UF, PdfValue.TextString(name));
        spec.Set(PdfNames.EF, PdfValue.Dictionary(files));

        if (!string.IsNullOrEmpty(description))
            spec.Set(PdfNames.Desc, PdfValue.TextString(description));

        // The relationship is always written, even when it is unspecified. Application Note 002
        // asks for it whenever the file is associated with anything, and a reader that finds no
        // entry at all cannot tell "nothing to say" from "the writer forgot".
        spec.Set(PdfNames.AFRelationship, PdfValue.Name(PdfName.Get(relationship.ToString())));

        PdfObjectId specId = _document.AddObject(PdfValue.Dictionary(spec));
        PdfValue reference = PdfValue.Reference(specId);

        AddToNameTree(name, reference);
        AddToAssociatedFiles(reference);
        return reference;
    }

    /// <summary>Attaches a file read from disk.</summary>
    /// <param name="path">The file to attach; its name is taken from the path.</param>
    /// <param name="mimeType">The media type.</param>
    /// <param name="description">A human-readable description.</param>
    /// <param name="relationship">How the file relates to the document.</param>
    public PdfValue AddFile(
        string path,
        string mimeType = "application/octet-stream",
        string? description = null,
        AttachmentRelationship relationship = AttachmentRelationship.Unspecified) =>
        Add(Path.GetFileName(path), File.ReadAllBytes(path), mimeType, description, relationship);

    private const string FacturXNamespace = "urn:factur-x:pdfa:CrossIndustryDocument:invoice:1p0#";

    /// <summary>
    /// Attaches a Factur-X or ZUGFeRD invoice: the XML that carries the same figures the pages show,
    /// named and related the way the hybrid invoice standards require.
    /// </summary>
    /// <param name="invoiceXml">The invoice document.</param>
    /// <param name="fileName">The file name the standard prescribes.</param>
    /// <param name="conformanceLevel">The Factur-X profile, such as <c>EN 16931</c>.</param>
    public PdfValue AddFacturX(
        ReadOnlyMemory<byte> invoiceXml,
        string fileName = "factur-x.xml",
        string conformanceLevel = "EN 16931")
    {
        PdfValue spec = Add(
            fileName,
            invoiceXml,
            "text/xml",
            "Factur-X invoice data",
            AttachmentRelationship.Data);

        var metadata = new Metadata.XmpMetadata();
        metadata.CopyFrom(_document.Info);
        metadata.AddSchema(BuildFacturXProperties(fileName, conformanceLevel));

        // The fx: namespace is nobody's predefined schema, so PDF/A wants it described alongside
        // the properties that use it. Without the description a hybrid invoice fails validation on
        // the one clause its whole point depends on.
        metadata.AddExtensionSchema(FacturXSchema);
        metadata.ApplyTo(_document);

        return spec;
    }

    private static readonly Metadata.XmpExtensionSchema FacturXSchema = new(
        "Factur-X PDFA Extension Schema",
        FacturXNamespace,
        "fx",
        [
            new Metadata.XmpSchemaProperty("DocumentFileName", "Text", "external", "name of the embedded XML invoice file"),
            new Metadata.XmpSchemaProperty("DocumentType", "Text", "external", "INVOICE"),
            new Metadata.XmpSchemaProperty("Version", "Text", "external", "the version of the standard the invoice follows"),
            new Metadata.XmpSchemaProperty("ConformanceLevel", "Text", "external", "the profile of the invoice data"),
        ]);

    private static string BuildFacturXProperties(string fileName, string conformanceLevel) =>
        $"<rdf:Description rdf:about=\"\" xmlns:fx=\"{FacturXNamespace}\">\n" +
        $"      <fx:DocumentType>INVOICE</fx:DocumentType>\n" +
        $"      <fx:DocumentFileName>{fileName}</fx:DocumentFileName>\n" +
        $"      <fx:Version>1.0</fx:Version>\n" +
        $"      <fx:ConformanceLevel>{conformanceLevel}</fx:ConformanceLevel>\n" +
        "    </rdf:Description>";

    /// <summary>
    /// Unlists a file specification: takes it out of the embedded files name tree and out of the
    /// catalogue's associated files. Detaching the annotation that showed the file is not enough on
    /// its own — the file would still travel with the document, invisible but complete.
    /// </summary>
    /// <param name="spec">The file specification to unlist, as returned when it was added.</param>
    /// <returns>Whether anything was listed under it.</returns>
    public bool Unlist(PdfValue spec)
    {
        bool removed = false;

        if (EmbeddedFilesNameTree(create: false)?.GetArray(PdfNames.Names) is { } names)
        {
            for (int i = names.Count - 2; i >= 0; i -= 2)
            {
                if (!Same(names[i + 1], spec))
                    continue;

                names.RemoveAt(i + 1);
                names.RemoveAt(i);
                removed = true;
            }
        }

        if (_document.Catalog.GetArray(PdfNames.AF) is { } associated)
        {
            for (int i = associated.Count - 1; i >= 0; i--)
            {
                if (!Same(associated[i], spec))
                    continue;

                associated.RemoveAt(i);
                removed = true;
            }
        }

        if (removed)
            _document.MarkChanged(_document.CatalogId);

        return removed;

        static bool Same(PdfValue left, PdfValue right) =>
            left.IsReference && right.IsReference
                ? left.AsReference.Number == right.AsReference.Number
                : ReferenceEquals(left.AsDictionary, right.AsDictionary);
    }

    /// <summary>
    /// Lists a file specification that already exists, rather than building one from content. This
    /// is what a merge needs: the specification and its stream have been copied over as they were,
    /// and all that remains is to put them in the tree under a name nothing else has taken.
    /// </summary>
    /// <param name="name">The name to list the file under.</param>
    /// <param name="spec">The file specification, already part of this document.</param>
    internal void Relist(string name, PdfValue spec)
    {
        if (spec.IsNull)
            return;

        // ISO 32000 requires a file-specification dictionary carrying EF to be indirect. Name-tree
        // enumeration resolves source references before merge import, so promote the copied direct
        // dictionary back into the target object table before listing it.
        if (spec.Kind == PdfValueKind.Dictionary)
            spec = PdfValue.Reference(_document.AddObject(spec));

        AddToNameTree(name, spec);
        AddToAssociatedFiles(spec);
    }

    private void AddToNameTree(string name, PdfValue spec)
    {
        PdfDictionary tree = EmbeddedFilesNameTree(create: true)!;
        PdfArray names = tree.GetArray(PdfNames.Names) ?? Create(tree);

        // A name tree's leaf is sorted by name, and readers rely on that order.
        int index = 0;
        while (index + 1 < names.Count &&
               string.CompareOrdinal(names.Get(index).AsTextString, name) < 0)
        {
            index += 2;
        }

        names.Insert(index, PdfValue.TextString(name));
        names.Insert(index + 1, spec);
        _document.MarkChanged(_document.CatalogId);

        static PdfArray Create(PdfDictionary tree)
        {
            var names = new PdfArray();
            tree.Set(PdfNames.Names, PdfValue.Array(names));
            return names;
        }
    }

    private void AddToAssociatedFiles(PdfValue spec)
    {
        PdfArray associated = _document.Catalog.GetArray(PdfNames.AF) ?? CreateAssociated();
        associated.Add(spec);
        _document.MarkChanged(_document.CatalogId);

        PdfArray CreateAssociated()
        {
            var array = new PdfArray();
            _document.Attach(PdfValue.Array(array));
            _document.Catalog.Set(PdfNames.AF, PdfValue.Array(array));
            return array;
        }
    }

    private PdfDictionary? EmbeddedFilesNameTree(bool create)
    {
        PdfDictionary? names = _document.Catalog.GetDictionary(PdfNames.Names);
        if (names is null)
        {
            if (!create)
                return null;

            names = new PdfDictionary(1);
            _document.Attach(PdfValue.Dictionary(names));
            _document.Catalog.Set(PdfNames.Names, PdfValue.Dictionary(names));
        }

        PdfDictionary? files = names.GetDictionary(PdfNames.EmbeddedFiles);
        if (files is null && create)
        {
            files = new PdfDictionary(1);
            _document.Attach(PdfValue.Dictionary(files));
            names.Set(PdfNames.EmbeddedFiles, PdfValue.Dictionary(files));
        }

        return files;
    }

    private static string Describe(ReadOnlyMemory<byte> content) =>
        Encoding.ASCII.GetString(MD5.HashData(content.Span));
}
