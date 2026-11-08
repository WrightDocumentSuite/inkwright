using Inkwright.Cos;
using Inkwright.Diagnostics;
using Inkwright.Filters;

namespace Inkwright.IO;

/// <summary>
/// Per-document resource bounds for reading untrusted PDFs. Each <see cref="PdfReader"/> gets its
/// own ledger, so changing limits for one request never changes another request's budget.
/// </summary>
public sealed class PdfResourceLimits
{
    /// <summary>Safe general-purpose limits used by <see cref="PdfReadOptions.Default"/>.</summary>
    public static PdfResourceLimits Default { get; } = new();

    /// <summary>Creates an immutable set of reader limits.</summary>
    /// <param name="maxInputBytes">Largest complete PDF that may be buffered.</param>
    /// <param name="maxDecodedBytesPerStream">Largest decoded result retained for one stream.</param>
    /// <param name="maxTotalDecodedBytes">Largest sum of decoded stream results retained lazily.</param>
    /// <param name="maxTotalDecodeWorkBytes">Largest cumulative output produced across filter stages.</param>
    /// <param name="maxFilterStages">Largest filter chain accepted for one stream.</param>
    /// <param name="maxCrossReferenceEntries">Largest cumulative number of xref rows processed.</param>
    /// <param name="maxCrossReferenceSections">Largest number of xref sections followed.</param>
    /// <param name="maxObjects">Largest number of distinct indirect objects indexed at once.</param>
    /// <param name="maxStructuralEntries">Largest cumulative number of object-stream entries allocated.</param>
    /// <param name="maxParsedValues">Largest cumulative number of COS values parsed lazily.</param>
    /// <param name="maxNestingDepth">Largest array/dictionary nesting depth, up to the parser hard limit.</param>
    public PdfResourceLimits(
        long maxInputBytes = 1024L * 1024 * 1024,
        long maxDecodedBytesPerStream = 128L * 1024 * 1024,
        long maxTotalDecodedBytes = 512L * 1024 * 1024,
        long maxTotalDecodeWorkBytes = 1024L * 1024 * 1024,
        int maxFilterStages = 64,
        int maxCrossReferenceEntries = 2_000_000,
        int maxCrossReferenceSections = 4_096,
        int maxObjects = 1_000_000,
        int maxStructuralEntries = 2_000_000,
        int maxParsedValues = 4_000_000,
        int maxNestingDepth = 128)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxInputBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxDecodedBytesPerStream);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxTotalDecodedBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxTotalDecodeWorkBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxFilterStages);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCrossReferenceEntries);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCrossReferenceSections);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxObjects);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxStructuralEntries);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxParsedValues);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxNestingDepth);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxNestingDepth, 128);

        MaxInputBytes = maxInputBytes;
        MaxDecodedBytesPerStream = maxDecodedBytesPerStream;
        MaxTotalDecodedBytes = maxTotalDecodedBytes;
        MaxTotalDecodeWorkBytes = maxTotalDecodeWorkBytes;
        MaxFilterStages = maxFilterStages;
        MaxCrossReferenceEntries = maxCrossReferenceEntries;
        MaxCrossReferenceSections = maxCrossReferenceSections;
        MaxObjects = maxObjects;
        MaxStructuralEntries = maxStructuralEntries;
        MaxParsedValues = maxParsedValues;
        MaxNestingDepth = maxNestingDepth;
    }

    /// <summary>Largest complete PDF that may be buffered.</summary>
    public long MaxInputBytes { get; }

    /// <summary>Largest decoded result retained for one stream.</summary>
    public long MaxDecodedBytesPerStream { get; }

    /// <summary>Largest sum of decoded stream results retained by this document.</summary>
    public long MaxTotalDecodedBytes { get; }

    /// <summary>Largest cumulative output produced across every general-purpose filter stage.</summary>
    public long MaxTotalDecodeWorkBytes { get; }

    /// <summary>Largest filter chain accepted for one stream.</summary>
    public int MaxFilterStages { get; }

    /// <summary>Largest cumulative number of classic or stream xref rows processed.</summary>
    public int MaxCrossReferenceEntries { get; }

    /// <summary>Largest number of xref sections followed through <c>Prev</c>/<c>XRefStm</c>.</summary>
    public int MaxCrossReferenceSections { get; }

    /// <summary>Largest number of distinct indirect objects indexed at once.</summary>
    public int MaxObjects { get; }

    /// <summary>Largest cumulative number of entries allocated for object streams.</summary>
    public int MaxStructuralEntries { get; }

    /// <summary>Largest cumulative number of COS values parsed lazily.</summary>
    public int MaxParsedValues { get; }

    /// <summary>Largest array/dictionary nesting depth.</summary>
    public int MaxNestingDepth { get; }
}

/// <summary>One document-local ledger shared by its lazy reader and decoded streams.</summary>
internal readonly record struct PdfEffectiveStreamLimit(int Value, string Name, long Limit);

internal sealed class PdfResourceBudget(PdfResourceLimits limits)
{
    internal const string ProcessWideDecodedBytesLimitName = "PdfFilterLimits.MaxDecodedBytes";
    internal const string RuntimeDecodedBytesLimitName = "DecodedStreamBufferCapacity";
    private const int MaxBufferBytes = int.MaxValue - 64;

    private readonly object _gate = new();
    private long _decodedBytes;
    private long _decodeWorkBytes;
    private int _xrefEntries;
    private int _xrefSections;
    private int _structuralEntries;
    private int _parsedValues;

    internal PdfResourceLimits Limits { get; } = limits;

    internal PdfEffectiveStreamLimit StreamLimit => SelectStreamLimit(Limits.MaxDecodedBytesPerStream);

    internal static PdfEffectiveStreamLimit ProcessWideStreamLimit() => SelectStreamLimit(long.MaxValue);

    internal void CheckInputLength(long length)
    {
        if (length < 0 || length > Limits.MaxInputBytes)
            throw Exceeded(nameof(PdfResourceLimits.MaxInputBytes), Limits.MaxInputBytes, length);
    }

    internal void EnsureObjectCapacity(int currentCount, int additional = 1)
    {
        if (currentCount < 0 || additional <= 0 || additional > Limits.MaxObjects - (long)currentCount)
        {
            throw Exceeded(
                nameof(PdfResourceLimits.MaxObjects),
                Limits.MaxObjects,
                (long)currentCount + additional);
        }
    }

    internal void ConsumeCrossReferenceEntries(int count) => Consume(
        ref _xrefEntries,
        count,
        Limits.MaxCrossReferenceEntries,
        nameof(PdfResourceLimits.MaxCrossReferenceEntries));

    internal void ConsumeCrossReferenceSection() => Consume(
        ref _xrefSections,
        1,
        Limits.MaxCrossReferenceSections,
        nameof(PdfResourceLimits.MaxCrossReferenceSections));

    internal void ConsumeStructuralEntries(int count) => Consume(
        ref _structuralEntries,
        count,
        Limits.MaxStructuralEntries,
        nameof(PdfResourceLimits.MaxStructuralEntries));

    internal void ConsumeParsedValues(int count) => Consume(
        ref _parsedValues,
        count,
        Limits.MaxParsedValues,
        nameof(PdfResourceLimits.MaxParsedValues));

    internal ReadOnlyMemory<byte> Decode(PdfStream stream, out PdfName? undecodedFilter)
    {
        lock (_gate)
        {
            PdfEffectiveStreamLimit streamLimit = StreamLimit;
            long decodedAvailable = Limits.MaxTotalDecodedBytes - _decodedBytes;
            long workAvailable = Limits.MaxTotalDecodeWorkBytes - _decodeWorkBytes;
            bool hasDecodeWork = HasDecodeWork(stream.Dictionary);
            long outputLimit = Math.Min(streamLimit.Value, decodedAvailable);
            if (outputLimit <= 0)
            {
                throw Exceeded(
                    nameof(PdfResourceLimits.MaxTotalDecodedBytes),
                    Limits.MaxTotalDecodedBytes,
                    MoreThan(Limits.MaxTotalDecodedBytes));
            }

            long decodeWorkLimit = hasDecodeWork ? workAvailable : long.MaxValue;
            if (decodeWorkLimit <= 0)
            {
                throw Exceeded(
                    nameof(PdfResourceLimits.MaxTotalDecodeWorkBytes),
                    Limits.MaxTotalDecodeWorkBytes,
                    MoreThan(Limits.MaxTotalDecodeWorkBytes));
            }

            PdfBoundedDecodeResult result = PdfFilterPipeline.DecodeCompatibilityBounded(
                stream.RawData,
                stream.Dictionary,
                streamLimit.Value,
                (int)outputLimit,
                decodeWorkLimit,
                Limits.MaxFilterStages);

            _decodeWorkBytes += result.ProducedBytes;
            switch (result.Status)
            {
                case PdfBoundedDecodeStatus.Complete:
                case PdfBoundedDecodeStatus.UndecodedFilter:
                    if (result.Data.Length > streamLimit.Value)
                    {
                        throw Exceeded(
                            streamLimit.Name,
                            streamLimit.Limit,
                            result.Data.Length);
                    }

                    if (result.Data.Length > decodedAvailable)
                    {
                        throw Exceeded(
                            nameof(PdfResourceLimits.MaxTotalDecodedBytes),
                            Limits.MaxTotalDecodedBytes,
                            _decodedBytes + result.Data.Length);
                    }

                    _decodedBytes += result.Data.Length;
                    undecodedFilter = result.TerminalFilter;
                    return result.Data;

                case PdfBoundedDecodeStatus.FilterChainLimit:
                    throw Exceeded(
                        nameof(PdfResourceLimits.MaxFilterStages),
                        Limits.MaxFilterStages,
                        (stream.Dictionary.GetFirst(PdfNames.Filter, PdfNames.F).AsArray?.Count ??
                         Limits.MaxFilterStages + 1L));

                case PdfBoundedDecodeStatus.LimitExceeded:
                    {
                        string name = result.ExceededLimit == PdfBoundedDecodeLimit.Work
                            ? nameof(PdfResourceLimits.MaxTotalDecodeWorkBytes)
                            : result.ExceededLimit == PdfBoundedDecodeLimit.RetainedOutput &&
                              outputLimit < streamLimit.Value
                                ? nameof(PdfResourceLimits.MaxTotalDecodedBytes)
                                : streamLimit.Name;
                        long limit = name switch
                        {
                            nameof(PdfResourceLimits.MaxTotalDecodedBytes) => Limits.MaxTotalDecodedBytes,
                            nameof(PdfResourceLimits.MaxTotalDecodeWorkBytes) => Limits.MaxTotalDecodeWorkBytes,
                            _ => streamLimit.Limit,
                        };
                        throw Exceeded(name, limit, MoreThan(limit));
                    }

                default:
                    throw new PdfFormatException(
                        $"A stream could not be decoded safely under the configured resource limits " +
                        $"({result.Status}, filter /{result.TerminalFilter?.Value ?? "unknown"}).");
            }
        }
    }

    private void Consume(ref int current, int count, int limit, string name)
    {
        lock (_gate)
        {
            if (count <= 0 || count > limit - (long)current)
                throw Exceeded(name, limit, (long)current + count);

            current += count;
        }
    }

    private static PdfResourceLimitException Exceeded(string name, long limit, long observed) =>
        new(name, limit, observed);

    private static long MoreThan(long limit) => limit == long.MaxValue ? limit : limit + 1;

    private static PdfEffectiveStreamLimit SelectStreamLimit(long requestLimit)
    {
        long processWideLimit = PdfFilterLimits.MaxDecodedBytes;
        if (requestLimit <= processWideLimit && requestLimit <= MaxBufferBytes)
        {
            return new PdfEffectiveStreamLimit(
                (int)requestLimit,
                nameof(PdfResourceLimits.MaxDecodedBytesPerStream),
                requestLimit);
        }

        if (processWideLimit <= MaxBufferBytes)
        {
            return new PdfEffectiveStreamLimit(
                (int)processWideLimit,
                ProcessWideDecodedBytesLimitName,
                processWideLimit);
        }

        return new PdfEffectiveStreamLimit(
            MaxBufferBytes,
            RuntimeDecodedBytesLimitName,
            MaxBufferBytes);
    }

    private static bool HasDecodeWork(PdfDictionary dictionary)
    {
        PdfValue value = dictionary.GetFirst(PdfNames.Filter, PdfNames.F);
        if (value.AsName is { } single)
            return StartsWork(single);

        if (value.AsArray is not { } filters)
            return false;

        for (int i = 0; i < filters.Count; i++)
        {
            if (filters.GetName(i) is not { } filter)
                continue;

            if (StartsWork(filter))
                return true;

            // Crypt is identity after decryption and the compatibility pipeline can continue.
            // Every other non-work filter is terminal (an image codec or unsupported filter), so
            // a suffix hidden behind it will never materialise bytes.
            if (filter.Value != "Crypt")
                return false;
        }

        return false;

        static bool StartsWork(PdfName filter) => filter.Value is
            "FlateDecode" or "Fl" or
            "LZWDecode" or "LZW" or
            "ASCII85Decode" or "A85" or
            "ASCIIHexDecode" or "AHx" or
            "RunLengthDecode" or "RL" or
            "BrotliDecode";
    }
}
