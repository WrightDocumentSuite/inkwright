using Inkwright.Cos;
using Inkwright.IO;
using Inkwright.Metadata;

namespace Inkwright.Layout;

/// <summary>Everything the archival profile needs that the document cannot work out for itself.</summary>
public sealed class PdfAOptions
{
    /// <summary>The name of the output condition, shown to a reader inspecting the intent.</summary>
    public string OutputConditionIdentifier { get; init; } = "sRGB IEC61966-2.1";

    /// <summary>A human-readable description of the output condition.</summary>
    public string OutputCondition { get; init; } = "sRGB";

    /// <summary>The registry the condition identifier belongs to.</summary>
    public string RegistryName { get; init; } = "http://www.color.org";

    /// <summary>The document language, written into the catalog and the metadata.</summary>
    public string? Language { get; init; }

    /// <summary>An ICC profile to use instead of the generated sRGB one.</summary>
    public byte[]? IccProfile { get; init; }

    /// <summary>How many colour components the supplied profile has.</summary>
    public int IccComponents { get; init; } = 3;
}

/// <summary>
/// Turns a document into an archival one. The profile adds what PDF/A requires — an output intent,
/// metadata that agrees with the information dictionary, and a version claim — and then checks the
/// document against the rules it cannot fix by itself, so a caller learns about an unembedded font
/// here rather than from a validator later.
/// </summary>
public static class PdfAProfile
{
    /// <summary>Applies the archival profile to a document.</summary>
    /// <param name="document">The document to convert.</param>
    /// <param name="conformance">Which level to claim.</param>
    /// <param name="options">Output condition and language details.</param>
    /// <exception cref="InvalidOperationException">The document breaks a rule the profile cannot fix.</exception>
    public static void Apply(PdfDocument document, PdfAConformance conformance, PdfAOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        options ??= new PdfAOptions();

        // Embedded fonts only write their programs when the document is about to be saved; the
        // archival check has to see the finished dictionaries, not the placeholders.
        document.FinishFonts();

        // Part 1 is written against PDF 1.4, which has neither object streams nor cross-reference
        // streams. Declaring the version is what keeps the writer from using them. Part 4 is
        // written against PDF 2.0 and requires a %PDF-2.n header outright (6.1.2).
        document.Version = conformance.Part switch
        {
            1 => PdfVersion.Pdf14,
            4 => PdfVersion.Pdf20,
            _ => PdfVersion.Pdf17,
        };

        document.ArchivalClaim = conformance.Name;

        // 6.1.3 all but removes the document information dictionary, which PDF 2.0 deprecated. It
        // survives only beside a PieceInfo entry, and then only to carry ModDate.
        if (conformance.Part == 4 && !document.Catalog.ContainsKey(PdfName.Get("PieceInfo")))
            document.SuppressInformationDictionary = true;

        AddFileIdentifier(document);
        AddOutputIntent(document, options);
        ApplyMetadata(document, conformance, options);

        IReadOnlyList<PdfAProblem> problems = Validate(document, conformance);
        if (problems.Count == 0)
            return;

        throw new InvalidOperationException(
            $"The document does not satisfy {conformance.Name}:\n  " +
            string.Join("\n  ", problems.Select(problem => problem.ToString())));
    }

    /// <summary>
    /// Reports the archival rules a document breaks, each named by the clause that states it. The
    /// checks cover what can be judged from the object graph and, when the document was read from a
    /// file, from its bytes.
    /// </summary>
    /// <param name="document">The document to check.</param>
    /// <param name="conformance">The level being claimed.</param>
    public static IReadOnlyList<PdfAProblem> Validate(PdfDocument document, PdfAConformance conformance)
    {
        ArgumentNullException.ThrowIfNull(document);
        return PdfAValidator.Validate(document, conformance);
    }

    /// <summary>Checks a file on disk against a conformance level.</summary>
    /// <param name="path">The file to check.</param>
    /// <param name="conformance">The level being claimed.</param>
    public static IReadOnlyList<PdfAProblem> ValidateFile(string path, PdfAConformance conformance)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Validate(File.ReadAllBytes(path), conformance);
    }

    /// <summary>Checks a file in memory against a conformance level.</summary>
    /// <param name="file">The complete file.</param>
    /// <param name="conformance">The level being claimed.</param>
    public static IReadOnlyList<PdfAProblem> Validate(byte[] file, PdfAConformance conformance)
    {
        ArgumentNullException.ThrowIfNull(file);
        return PdfAValidator.Validate(file, conformance);
    }

    /// <summary>
    /// The year written into <c>pdfaid:rev</c>: the publication year of ISO 19005-4:2020. A dated
    /// revision of part 4 would change this and nothing else.
    /// </summary>
    internal const int SupportedPartFourRevision = 2020;

    private static void ApplyMetadata(PdfDocument document, PdfAConformance conformance, PdfAOptions options)
    {
        var metadata = new XmpMetadata
        {
            PdfAPart = conformance.Part,

            // Part 4 states the level only for its two extensions, and requires the revision year
            // that the earlier parts have no property for (6.7.3).
            PdfAConformance = conformance.Level == '\0' ? null : conformance.Level.ToString(),
            PdfARevision = conformance.Part == 4 ? SupportedPartFourRevision : null,
            Language = options.Language,
        };

        metadata.CopyFrom(document.Info);
        metadata.CreationDate ??= DateTimeOffset.Now;
        metadata.ModificationDate ??= metadata.CreationDate;

        // The two have to tell the same story, and the save is about to stamp the information
        // dictionary with the producer name; writing the same one here keeps them in step.
        metadata.Producer = PdfDocument.ProducerName;
        document.Info.Producer = PdfDocument.ProducerName;
        document.Info.CreationDate ??= metadata.CreationDate;
        document.Info.ModificationDate = metadata.ModificationDate;
        metadata.ApplyTo(document);

        if (string.IsNullOrEmpty(options.Language))
            return;

        document.Catalog.Set(PdfName.Get("Lang"), PdfValue.TextString(options.Language));
        document.MarkChanged(document.CatalogId);
    }

    /// <summary>
    /// The trailer identifier is what tells two copies of a document apart, so PDF/A requires it.
    /// Writing it here rather than leaving it to the save means the claim holds for the document in
    /// memory as well as for the file it becomes.
    /// </summary>
    private static void AddFileIdentifier(PdfDocument document)
    {
        if (document.Trailer.GetArray(PdfName.Get("ID")) is { Count: 2 })
            return;

        byte[] permanent = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
        var identifier = new PdfArray(
            PdfValue.String(permanent, hex: true),
            PdfValue.String(permanent, hex: true));

        document.Attach(PdfValue.Array(identifier));
        document.Trailer.Set(PdfName.Get("ID"), PdfValue.Array(identifier));
    }

    private static void AddOutputIntent(PdfDocument document, PdfAOptions options)
    {
        byte[] profile = options.IccProfile ?? Metadata.IccProfile.CreateSRgb();

        var stream = PdfStream.FromData(profile);
        stream.Dictionary.Set(PdfName.Get("N"), PdfValue.Integer(options.IccComponents));
        PdfObjectId profileId = document.AddObject(PdfValue.Stream(stream));

        var intent = new PdfDictionary(6);
        intent.Set(PdfName.Get("Type"), PdfValue.Name("OutputIntent"));
        intent.Set(PdfName.Get("S"), PdfValue.Name("GTS_PDFA1"));
        intent.Set(PdfName.Get("OutputConditionIdentifier"), PdfValue.TextString(options.OutputConditionIdentifier));
        intent.Set(PdfName.Get("OutputCondition"), PdfValue.TextString(options.OutputCondition));
        intent.Set(PdfName.Get("RegistryName"), PdfValue.TextString(options.RegistryName));
        intent.Set(PdfName.Get("DestOutputProfile"), PdfValue.Reference(profileId));

        PdfObjectId intentId = document.AddObject(PdfValue.Dictionary(intent));

        var intents = new PdfArray(PdfValue.Reference(intentId));
        document.Attach(PdfValue.Array(intents));
        document.Catalog.Set(PdfName.Get("OutputIntents"), PdfValue.Array(intents));
        document.MarkChanged(document.CatalogId);
    }
}
