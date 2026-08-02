using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Xml.Linq;
using Inkwright.Cos;
using Inkwright.Diagnostics;
using Inkwright.Filters;
using Inkwright.IO;

namespace Inkwright.Layout;

/// <summary>Bounds recursive validation of PDFs embedded in another PDF/A file.</summary>
internal sealed class PdfAValidationLimits
{
    internal PdfAValidationLimits(
        int maxDepth = 16,
        int maxEmbeddedFiles = 128,
        long maxTotalDecodedBytes = 128L * 1024 * 1024,
        int maxGraphValues = 1_048_576,
        int? maxParsedValues = null,
        long maxTotalXmpBytes = 16L * 1024 * 1024,
        long maxTotalXmpCharacters = 16L * 1024 * 1024)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxDepth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxEmbeddedFiles);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxTotalDecodedBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxGraphValues);
        if (maxParsedValues is not null)
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxParsedValues.Value);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxTotalXmpBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxTotalXmpCharacters);

        MaxDepth = maxDepth;
        MaxEmbeddedFiles = maxEmbeddedFiles;
        MaxTotalDecodedBytes = maxTotalDecodedBytes;
        MaxGraphValues = maxGraphValues;
        MaxParsedValues = maxParsedValues ?? maxGraphValues;
        MaxTotalXmpBytes = maxTotalXmpBytes;
        MaxTotalXmpCharacters = maxTotalXmpCharacters;
    }

    internal static PdfAValidationLimits Default { get; } = new();

    internal int MaxDepth { get; }

    internal int MaxEmbeddedFiles { get; }

    internal long MaxTotalDecodedBytes { get; }

    internal int MaxGraphValues { get; }

    internal int MaxParsedValues { get; }

    internal long MaxTotalXmpBytes { get; }

    internal long MaxTotalXmpCharacters { get; }
}

/// <summary>
/// Resource ledgers shared by the root and every embedded document. A path-local content
/// fingerprint catches a self-containing PDF quine, while monotonic count and byte totals bound a
/// wide tree.
/// </summary>
internal sealed partial class PdfAValidationSession
{
    private readonly PdfAValidationLimits _limits;
    private readonly HashSet<string> _activeFiles = new(StringComparer.Ordinal);
    private bool _resourceBudgetExhausted;
    private int _depth;
    private int _embeddedFiles;
    private long _decodedBytes;
    private long _decodeWorkBytes;
    private long _encodedInputBytes;
    private int _graphValues;
    private int _parsedValues;
    private int _structuralEntries;
    private bool _graphBudgetExhausted;

    internal PdfAValidationSession(PdfAValidationLimits limits, ReadOnlyMemory<byte> rootBytes)
    {
        _limits = limits;

        if (!rootBytes.IsEmpty)
            _activeFiles.Add(Fingerprint(rootBytes.Span));
    }

    internal bool TryReserve(
        string path,
        long? declaredSize,
        out PdfAValidationReservation? reservation,
        out string? problem)
    {
        reservation = null;

        // Once the count or byte budget is exhausted, later candidates are skipped before their
        // streams are decoded. The first candidate at the boundary already reported why.
        if (_resourceBudgetExhausted)
        {
            problem = null;
            return false;
        }

        if (_depth >= _limits.MaxDepth)
        {
            problem =
                $"Recursive PDF/A validation stopped at '{path}': the nesting limit " +
                $"of {_limits.MaxDepth} embedded PDF levels was reached.";
            return false;
        }

        if (_embeddedFiles >= _limits.MaxEmbeddedFiles)
        {
            _resourceBudgetExhausted = true;
            problem =
                $"Recursive PDF/A validation stopped at '{path}': the file-count limit " +
                $"of {_limits.MaxEmbeddedFiles} embedded PDFs was reached.";
            return false;
        }

        long availableBytes = Math.Min(
            Math.Min(AvailableDecodedBytes, AvailableDecodeWorkBytes),
            PdfFilterLimits.Cap);
        if (availableBytes <= 0)
        {
            _resourceBudgetExhausted = true;
            problem = ByteBudgetProblem(path);
            return false;
        }

        if (declaredSize is > 0 && declaredSize > availableBytes)
        {
            _resourceBudgetExhausted = true;
            problem = ByteBudgetProblem(path);
            return false;
        }

        _depth++;
        _embeddedFiles++;
        reservation = new PdfAValidationReservation(this, path);
        problem = null;
        return true;
    }

    internal bool TryAccept(
        PdfAValidationReservation reservation,
        ReadOnlyMemory<byte> bytes,
        out string? problem)
    {
        if (bytes.Length > _limits.MaxTotalDecodedBytes - _decodedBytes)
        {
            _resourceBudgetExhausted = true;
            problem =
                $"Recursive PDF/A validation stopped at '{reservation.Path}': the decoded-byte budget " +
                $"of {_limits.MaxTotalDecodedBytes} bytes was exceeded.";
            return false;
        }

        string fingerprint = Fingerprint(bytes.Span);
        if (!_activeFiles.Add(fingerprint))
        {
            problem =
                $"Recursive PDF/A validation stopped at '{reservation.Path}': the embedded bytes repeat an " +
                "active ancestor, forming a content cycle.";
            return false;
        }

        _decodedBytes += bytes.Length;
        reservation.Fingerprint = fingerprint;
        problem = null;
        return true;
    }

    internal int AttachmentCandidateLimit => Math.Max(_limits.MaxEmbeddedFiles, 1_024);

    internal int MaxStructuralEntries => _limits.MaxGraphValues;

    internal IPdfStreamDecodePolicy CreateDecodePolicy(string path) =>
        new PdfAStreamDecodePolicy(this, path);

    internal void ConsumeStructuralEntries(int count, string path)
    {
        if (count <= 0 || count > _limits.MaxGraphValues - _structuralEntries)
        {
            throw new PdfFormatException(
                $"Embedded PDF '{path}' exceeds the shared protected-reader limit of " +
                $"{_limits.MaxGraphValues} structural entries.");
        }

        _structuralEntries += count;
    }

    internal void ConsumeParsedValues(int count, string path)
    {
        if (count <= 0 || count > _limits.MaxParsedValues - _parsedValues)
        {
            throw new PdfFormatException(
                $"Embedded PDF '{path}' exceeds the protected reader limit of " +
                $"{_limits.MaxParsedValues} parsed values.");
        }

        _parsedValues += count;
    }

    internal bool TryConsumeGraphValue(string embeddedPath, out string? problem)
    {
        if (_graphBudgetExhausted)
        {
            problem = null;
            return false;
        }

        if (_graphValues >= _limits.MaxGraphValues)
        {
            _graphBudgetExhausted = true;
            string location = string.IsNullOrEmpty(embeddedPath)
                ? "the root document"
                : $"embedded path '{embeddedPath}'";
            problem =
                $"Object-graph validation stopped in {location}: the shared limit of " +
                $"{_limits.MaxGraphValues} reachable values was reached, so conformance cannot be established.";
            return false;
        }

        _graphValues++;
        problem = null;
        return true;
    }

    internal int DecodedByteLimit => (int)Math.Min(
        Math.Min(AvailableDecodedBytes, AvailableDecodeWorkBytes),
        PdfFilterLimits.Cap);

    private int DecodeWorkByteLimit => (int)Math.Min(
        AvailableDecodeWorkBytes,
        PdfFilterLimits.Cap);

    internal string ByteBudgetProblem(string path, int? perStreamLimit = null) =>
        $"Recursive PDF/A validation stopped at '{path}': the per-stream safety limit of " +
        $"{perStreamLimit ?? DecodedByteLimit} bytes for decoded output or cumulative decode work was exceeded.";

    internal string ExhaustByteBudget(string path, int perStreamLimit)
    {
        string problem = ByteBudgetProblem(path, perStreamLimit);
        _resourceBudgetExhausted = true;
        return problem;
    }

    private long AvailableDecodedBytes => _limits.MaxTotalDecodedBytes - _decodedBytes;

    private long AvailableDecodeWorkBytes => _limits.MaxTotalDecodedBytes - _decodeWorkBytes;

    private long AvailableEncodedInputBytes => _limits.MaxTotalDecodedBytes - _encodedInputBytes;

    internal bool TryChargeEncodedInput(long encodedBytes, string path, out string? problem)
    {
        if (encodedBytes < 0 || encodedBytes > AvailableEncodedInputBytes)
        {
            _resourceBudgetExhausted = true;
            problem =
                $"Recursive PDF/A validation stopped at '{path}': the shared encoded-input budget " +
                $"of {_limits.MaxTotalDecodedBytes} bytes was exceeded.";
            return false;
        }

        _encodedInputBytes += encodedBytes;
        problem = null;
        return true;
    }

    internal bool TryChargeDecodeWork(long producedBytes, string path, out string? problem)
    {
        if (producedBytes < 0 || producedBytes > AvailableDecodeWorkBytes)
        {
            _resourceBudgetExhausted = true;
            problem =
                $"Recursive PDF/A validation stopped at '{path}': the cumulative decode-work budget " +
                $"of {_limits.MaxTotalDecodedBytes} bytes was exceeded.";
            return false;
        }

        _decodeWorkBytes += producedBytes;
        problem = null;
        return true;
    }

    internal ReadOnlyMemory<byte> DecodeEmbeddedStream(
        PdfStream stream,
        string path,
        out PdfName? undecodedFilter)
    {
        undecodedFilter = null;
        int decodeLimit = PdfFilterLimits.Cap;
        PdfBoundedDecodeResult decoded = DecodeEmbeddedStreamBounded(stream, path, decodeLimit);

        bool structuralStream = stream.Dictionary.GetName(PdfNames.Type) is { } type &&
                                (type == PdfNames.XRef || type == PdfNames.ObjStm);

        switch (decoded.Status)
        {
            case PdfBoundedDecodeStatus.Complete:
                return decoded.Data;

            case PdfBoundedDecodeStatus.UndecodedFilter
                when !structuralStream &&
                     decoded.TerminalFilter is { } terminal &&
                     PdfFilterPipeline.IsStrictImageCodec(terminal):
                // The bytes have passed every general-purpose filter. They remain encoded in the
                // final image codec; PdfStream exposes that terminal codec to image consumers.
                undecodedFilter = terminal;
                return decoded.Data;

            case PdfBoundedDecodeStatus.LimitExceeded:
                _resourceBudgetExhausted = true;
                throw new PdfFormatException(
                    $"Recursive PDF/A validation stopped at '{path}': an internal stream exceeded the " +
                    $"remaining decode-work limit of {decodeLimit} bytes.");

            case PdfBoundedDecodeStatus.FilterChainLimit:
                throw new PdfFormatException(
                    $"Embedded PDF '{path}' contains an internal stream with more than 64 filters.");

            case PdfBoundedDecodeStatus.InvalidParameters:
                throw new PdfFormatException(
                    $"Embedded PDF '{path}' contains an internal stream with malformed DecodeParms, " +
                    "invalid filter ordering, or a non-terminal image codec.");

            case PdfBoundedDecodeStatus.InvalidData:
                throw new PdfFormatException(
                    $"Embedded PDF '{path}' contains invalid or truncated data for the " +
                    $"{decoded.TerminalFilter?.Value ?? "stream filter"} filter.");

            default:
                throw new PdfFormatException(
                    $"Embedded PDF '{path}' contains an internal stream whose " +
                    $"{decoded.TerminalFilter?.Value ?? "unknown"} filter cannot be decoded safely.");
        }
    }

    internal PdfBoundedDecodeResult DecodeEmbeddedStreamBounded(
        PdfStream stream,
        string path,
        int maxDecodedBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxDecodedBytes);

        if (stream.Dictionary.ContainsKey(PdfNames.F) ||
            stream.Dictionary.ContainsKey(PdfName.Get("FFilter")) ||
            stream.Dictionary.ContainsKey(PdfName.Get("FDecodeParms")))
        {
            throw new PdfFormatException(
                $"Embedded PDF '{path}' contains a stream with external-file stream keys F, " +
                "FFilter, or FDecodeParms, which protected recursive validation does not permit.");
        }

        if (!TryChargeEncodedInput(stream.RawLength, path, out string? rawProblem))
            throw new PdfFormatException(rawProblem!);

        PdfValue filterValue = stream.Dictionary.GetFirst(PdfNames.Filter, PdfNames.F);
        if (ContainsForbiddenArchivalFilter(filterValue))
        {
            throw new PdfFormatException(
                $"Embedded PDF '{path}' contains an internal stream using LZWDecode or the " +
                "non-standard BrotliDecode filter, which PDF/A does not permit.");
        }

        int sharedLimit = DecodeWorkByteLimit;
        if (sharedLimit <= 0)
        {
            _resourceBudgetExhausted = true;
            throw new PdfFormatException(
                $"Recursive PDF/A validation stopped at '{path}': the cumulative decode-work budget " +
                $"of {_limits.MaxTotalDecodedBytes} bytes was exhausted before an internal stream could be decoded.");
        }

        int decodeLimit = Math.Min(sharedLimit, maxDecodedBytes);
        PdfBoundedDecodeResult decoded;
        try
        {
            decoded = stream.DecodeBounded(decodeLimit);
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException or
            OperationCanceledException or AccessViolationException))
        {
            throw new PdfFormatException(
                $"Embedded PDF '{path}' contains an internal stream that could not be decoded safely.",
                exception);
        }

        if (!TryChargeDecodeWork(decoded.ProducedBytes, path, out string? workProblem))
            throw new PdfFormatException(workProblem!);

        if (decoded.Status == PdfBoundedDecodeStatus.LimitExceeded && sharedLimit <= maxDecodedBytes)
        {
            _resourceBudgetExhausted = true;
            throw new PdfFormatException(
                $"Recursive PDF/A validation stopped at '{path}': an internal stream exceeded the " +
                $"remaining decode-work limit of {sharedLimit} bytes.");
        }

        return decoded;

        static bool ContainsForbiddenArchivalFilter(PdfValue value)
        {
            if (value.AsName is { } single)
                return Forbidden(single);

            if (value.AsArray is not { } array)
                return false;

            for (int i = 0; i < array.Count; i++)
            {
                if (array.Get(i).AsName is { } filter && Forbidden(filter))
                    return true;
            }

            return false;

            static bool Forbidden(PdfName filter) =>
                filter.Value is "LZWDecode" or "LZW" or "BrotliDecode";
        }
    }

    private static string Fingerprint(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes));

    internal void Exit(PdfAValidationReservation reservation)
    {
        if (reservation.Fingerprint is { } fingerprint)
            _activeFiles.Remove(fingerprint);

        _depth--;
    }
}

/// <summary>A pre-decode slot in the shared recursive-validation budget.</summary>
internal sealed class PdfAValidationReservation(
    PdfAValidationSession owner,
    string path) : IDisposable
{
    private bool _disposed;

    internal string Path { get; } = path;

    internal string? Fingerprint { get; set; }

    internal int DecodedByteLimit => owner.DecodedByteLimit;

    internal string ByteBudgetProblem => owner.ByteBudgetProblem(Path);

    internal string ExhaustByteBudget(int perStreamLimit) => owner.ExhaustByteBudget(Path, perStreamLimit);

    internal bool TryChargeDecodeWork(long producedBytes, out string? problem) =>
        owner.TryChargeDecodeWork(producedBytes, Path, out problem);

    internal bool TryChargeEncodedInput(long encodedBytes, out string? problem) =>
        owner.TryChargeEncodedInput(encodedBytes, Path, out problem);

    internal bool TryAccept(ReadOnlyMemory<byte> bytes, out string? problem) =>
        owner.TryAccept(this, bytes, out problem);

    internal IPdfStreamDecodePolicy CreateDecodePolicy() =>
        owner.CreateDecodePolicy(Path);

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        owner.Exit(this);
    }
}

/// <summary>Strict reader policy for every stream parsed from one recursively opened PDF.</summary>
internal sealed class PdfAStreamDecodePolicy(
    PdfAValidationSession validation,
    string path) : IPdfStreamDecodePolicy
{
    public int MaxStructuralEntries => validation.MaxStructuralEntries;

    public void ConsumeStructuralEntries(int count) => validation.ConsumeStructuralEntries(count, path);

    public void ConsumeParsedValues(int count) => validation.ConsumeParsedValues(count, path);

    public ReadOnlyMemory<byte> Decode(PdfStream stream, out PdfName? undecodedFilter) =>
        validation.DecodeEmbeddedStream(stream, path, out undecodedFilter);

    public PdfBoundedDecodeResult DecodeBounded(PdfStream stream, int maxDecodedBytes) =>
        validation.DecodeEmbeddedStreamBounded(stream, path, maxDecodedBytes);
}

/// <summary>
/// Runs every archival rule the library knows over a document. The groups follow the structure of
/// clause 6 of the standard rather than the structure of the code, so a reader with the text open
/// can find the check that matches the paragraph in front of them.
/// </summary>
internal static class PdfAValidator
{
    private const int MaxPropagatedProblemsPerEmbeddedFile = 64;

    internal static IReadOnlyList<PdfAProblem> Validate(PdfDocument document, PdfAConformance conformance) =>
        Validate(document, conformance, PdfAValidationLimits.Default);

    internal static IReadOnlyList<PdfAProblem> Validate(byte[] file, PdfAConformance conformance) =>
        Validate(file, conformance, PdfAValidationLimits.Default);

    internal static IReadOnlyList<PdfAProblem> Validate(
        byte[] file,
        PdfAConformance conformance,
        PdfAValidationLimits limits)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(limits);

        if (RootTrailerProblem(file) is { } trailerProblem)
            return [trailerProblem];

        var validation = new PdfAValidationSession(limits, file);
        PdfWarning? firstStructuralWarning = null;

        try
        {
            using PdfDocument document = PdfDocument.Load(
                file,
                new PdfReadOptions
                {
                    OpenLockedDocuments = true,
                    Recover = false,
                    StreamDecodePolicy = validation.CreateDecodePolicy("(root document)"),
                    OnWarning = warning =>
                    {
                        if (firstStructuralWarning is null && IsStructuralWarning(warning.Code))
                            firstStructuralWarning = warning;
                    },
                });

            IReadOnlyList<PdfAProblem> problems =
                ValidateCore(document, conformance, validation, embeddedPath: string.Empty);
            if (TryGetStructuralWarning(
                    document.Diagnostics,
                    firstStructuralWarning,
                    out string? structuralWarning))
            {
                return problems.Concat(
                [
                    new PdfAProblem(
                        "6.1",
                        "The PDF produced a structural reader warning during validation and " +
                        $"cannot be accepted as PDF/A: {structuralWarning}"),
                ]).ToArray();
            }

            return problems;
        }
        catch (Exception exception) when (IsRecoverableEmbeddedFailure(exception))
        {
            return
            [
                new PdfAProblem(
                    "6.1",
                    $"The PDF cannot be opened or validated safely: {exception.Message}"),
            ];
        }
    }

    private static PdfAProblem? RootTrailerProblem(ReadOnlySpan<byte> file)
    {
        int marker = file.LastIndexOf("%%EOF"u8);
        if (marker < 0)
            return new PdfAProblem("6.1.3", "The file has no %%EOF marker.");

        ReadOnlySpan<byte> tail = file[(marker + 5)..];
        bool clean = tail.Length == 0 ||
                     tail.SequenceEqual("\n"u8) ||
                     tail.SequenceEqual("\r"u8) ||
                     tail.SequenceEqual("\r\n"u8);
        return clean
            ? null
            : new PdfAProblem(
                "6.1.3",
                $"{tail.Length} bytes follow the last %%EOF; only one end-of-line marker may.");
    }

    internal static IReadOnlyList<PdfAProblem> Validate(
        PdfDocument document,
        PdfAConformance conformance,
        PdfAValidationLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);

        ReadOnlyMemory<byte> rootBytes = document.Reader?.FileBytes ?? default;
        var validation = new PdfAValidationSession(limits, rootBytes);
        return ValidateCore(document, conformance, validation, embeddedPath: string.Empty);
    }

    /// <summary>Reserves recursion/count/declared-size capacity before a stream is decoded.</summary>
    internal static PdfAValidationReservation? BeginEmbeddedPdf(
        PdfAContext parent,
        string name,
        string clause,
        long? declaredSize)
    {
        string path = JoinPath(parent.EmbeddedPath, name);

        if (!parent.Validation.TryReserve(
                path,
                declaredSize,
                out PdfAValidationReservation? reservation,
                out string? budgetProblem))
        {
            if (budgetProblem is not null)
                parent.Report(clause, budgetProblem);

            return null;
        }

        return reservation;
    }

    /// <summary>
    /// Strictly validates the storage filters of an arbitrary embedded file without treating its
    /// payload as PDF or consuming a recursive file, depth, decoded-byte, or fingerprint slot.
    /// </summary>
    internal static void ValidateArbitraryEmbeddedFile(
        PdfAContext parent,
        PdfStream stream,
        string name)
    {
        string path = JoinPath(parent.EmbeddedPath, name);
        string clause = parent.Clause("6.1.10", "6.1.7.2", "6.1.6.2");
        PdfBoundedDecodeResult decoded;

        try
        {
            if (!stream.TryGetPolicyDecoded(out decoded))
            {
                decoded = parent.Validation.DecodeEmbeddedStreamBounded(
                    stream,
                    path,
                    PdfFilterLimits.Cap);
            }
        }
        catch (PdfFormatException exception)
        {
            parent.Report(
                clause,
                $"Embedded file '{path}' cannot be validated safely: {exception.Message}");
            return;
        }

        switch (decoded.Status)
        {
            case PdfBoundedDecodeStatus.Complete:
                return;

            case PdfBoundedDecodeStatus.UndecodedFilter
                when decoded.TerminalFilter is { } terminal &&
                     PdfFilterPipeline.IsStrictImageCodec(terminal):
                // General-purpose prefixes have been decoded and charged. The terminal image
                // codec is intentionally left in its native representation.
                return;

            case PdfBoundedDecodeStatus.LimitExceeded:
                parent.Report(
                    clause,
                    $"Embedded file '{path}' exceeded the bounded stream-decode limit.");
                return;

            case PdfBoundedDecodeStatus.FilterChainLimit:
                parent.Report(
                    clause,
                    $"Embedded file '{path}' has more than 64 stream filters and cannot be " +
                    "validated safely.");
                return;

            case PdfBoundedDecodeStatus.InvalidParameters:
                parent.Report(
                    clause,
                    $"Embedded file '{path}' has malformed DecodeParms, invalid filter ordering, " +
                    "or a non-terminal image codec.");
                return;

            case PdfBoundedDecodeStatus.InvalidData:
                parent.Report(
                    clause,
                    $"Embedded file '{path}' contains invalid or truncated data for the " +
                    $"{decoded.TerminalFilter?.Value ?? "stream filter"} filter.");
                return;

            default:
                parent.Report(
                    clause,
                    $"Embedded file '{path}' uses " +
                    $"{decoded.TerminalFilter?.Value ?? "a non-name or unsupported filter"} and " +
                    "cannot be decoded safely.");
                return;
        }
    }

    /// <summary>Validates one decoded embedded-file stream as a separately conforming PDF/A file.</summary>
    internal static void ValidateEmbeddedPdf(
        PdfAContext parent,
        ReadOnlyMemory<byte> file,
        string clause,
        PdfAValidationReservation reservation)
    {
        string path = reservation.Path;

        if (!reservation.TryAccept(file, out string? budgetProblem))
        {
            parent.Report(clause, budgetProblem!);
            return;
        }

        if (!LooksLikePdf(file.Span))
        {
            parent.Report(
                clause,
                $"Embedded PDF '{path}' is not a PDF: its decoded bytes do not begin with a PDF header.");
            return;
        }

        try
        {
            byte[] bytes = MemoryMarshal.TryGetArray(file, out ArraySegment<byte> segment) &&
                           segment.Offset == 0 && segment.Count == segment.Array!.Length
                ? segment.Array
                : file.ToArray();
            PdfWarning? firstStructuralWarning = null;
            using PdfDocument embedded = PdfDocument.Load(
                bytes,
                new PdfReadOptions
                {
                    OpenLockedDocuments = true,
                    Recover = false,
                    StreamDecodePolicy = reservation.CreateDecodePolicy(),
                    OnWarning = warning =>
                    {
                        if (firstStructuralWarning is null && IsStructuralWarning(warning.Code))
                            firstStructuralWarning = warning;
                    },
                });

            if (embedded.WasEncrypted)
            {
                parent.Report(
                    clause,
                    $"Embedded PDF '{path}' is encrypted. Encryption is forbidden by every PDF/A part.");
                return;
            }

            if (TryGetStructuralWarning(
                    embedded.Diagnostics,
                    firstStructuralWarning,
                    out string? structuralWarning))
            {
                parent.Report(
                    clause,
                    $"Embedded PDF '{path}' required structural reader recovery and cannot be " +
                    $"accepted as PDF/A: {structuralWarning}");
                return;
            }

            if (!TryReadClaim(
                    embedded,
                    parent.Validation,
                    out PdfAConformance conformance,
                    out string? claimProblem))
            {
                parent.Report(
                    clause,
                    $"Embedded PDF '{path}' has no usable PDF/A identification: {claimProblem}");
                return;
            }

            if (!PartIsAllowed(parent, conformance))
            {
                string allowed = parent.Part == 2 ? "ISO 19005 parts 1 or 2" : "ISO 19005 parts 1, 2 or 4";
                parent.Report(
                    clause,
                    $"Embedded PDF '{path}' claims {conformance.Name}; {parent.Conformance.Name} permits " +
                    $"embedded PDFs only when they conform to {allowed}.");
                return;
            }

            IReadOnlyList<PdfAProblem> nested =
                ValidateCore(embedded, conformance, parent.Validation, path);
            if (TryGetStructuralWarning(
                    embedded.Diagnostics,
                    firstStructuralWarning,
                    out structuralWarning))
            {
                parent.Report(
                    clause,
                    $"Embedded PDF '{path}' produced a structural reader warning during validation " +
                    $"and cannot be accepted as PDF/A: {structuralWarning}");
                return;
            }

            int reported = Math.Min(nested.Count, MaxPropagatedProblemsPerEmbeddedFile);

            for (int i = 0; i < reported; i++)
            {
                PdfAProblem problem = nested[i];
                parent.Report(
                    clause,
                    $"Embedded PDF '{path}' does not conform to {conformance.Name}: " +
                    $"ISO 19005 §{problem.Clause}: {problem.Message}");
            }

            if (nested.Count > reported)
            {
                parent.Report(
                    clause,
                    $"Embedded PDF '{path}' has {nested.Count - reported} additional conformance " +
                    "problems omitted by the recursive-reporting limit.");
            }
        }
        catch (Exception exception) when (IsRecoverableEmbeddedFailure(exception))
        {
            parent.Report(
                clause,
                $"Embedded PDF '{path}' cannot be opened or validated as PDF: {exception.Message}");
        }
    }

    private static IReadOnlyList<PdfAProblem> ValidateCore(
        PdfDocument document,
        PdfAConformance conformance,
        PdfAValidationSession validation,
        string embeddedPath)
    {
        var context = new PdfAContext(document, conformance, validation, embeddedPath);

        PdfARules.CheckFileStructure(context);

        // Encryption alone sinks the claim. When the file was opened locked the strings and
        // streams are still ciphertext, and rules read over ciphertext report gibberish; when it
        // was decrypted, everything else it says is moot until the file is saved without the
        // cipher anyway.
        if (document.WasEncrypted)
            return context.Problems;

        if (!PreflightProtectedStreams(context))
            return context.Problems;

        PdfARules.CheckSyntax(context);
        PdfARules.CheckNames(context);
        PdfARules.CheckGraphics(context);
        PdfARules.CheckFonts(context);
        PdfARules.CheckInteractive(context);
        PdfARules.CheckMetadata(context);
        PdfARules.CheckLogicalStructure(context);
        PdfARules.CheckAttachments(context);
        PdfARules.CheckSignatures(context);

        return context.Problems;
    }

    /// <summary>
    /// Forces every live internal stream of a protected document through its strict reader policy
    /// before any rule consumes it. Embedded-file payloads keep their dedicated recursive
    /// reservation and are therefore excluded by stream identity when they are real attachments.
    /// </summary>
    private static bool PreflightProtectedStreams(PdfAContext context)
    {
        if (context.Document.Reader is not { UsesProtectedDecoding: true })
            return true;

        string clause = context.Clause("6.1.10", "6.1.7.2", "6.1.6.2");
        try
        {
            HashSet<PdfStream> embeddedPayloads = EmbeddedPayloadsExcludedFromPreflight(context);

            IReadOnlyList<PdfStream> streams = context.FileStreams;
            for (int i = 0; i < streams.Count; i++)
            {
                PdfStream stream = streams[i];
                if (!embeddedPayloads.Contains(stream))
                    _ = stream.Decode();
            }

            return true;
        }
        catch (PdfFormatException exception)
        {
            string location = string.IsNullOrEmpty(context.EmbeddedPath)
                ? "The root PDF"
                : $"Embedded PDF '{context.EmbeddedPath}'";
            context.Report(
                clause,
                $"{location} has a reachable internal stream or another xref-live stream that cannot " +
                $"be validated safely: {exception.Message}");
            return false;
        }
    }

    private static HashSet<PdfStream> EmbeddedPayloadsExcludedFromPreflight(PdfAContext context)
    {
        var payloads = new HashSet<PdfStream>(ReferenceEqualityComparer.Instance);

        if (context.Part != 4)
        {
            foreach (PdfDictionary dictionary in context.FileDictionaries)
                AddFromSpecification(dictionary);

            return payloads;
        }

        // Part 4 applies §6.9 only to file specifications in Catalog/Names/EmbeddedFiles. An
        // arbitrary reachable dictionary named EF must not exempt an internal stream from strict
        // preflight merely by looking attachment-like.
        if (context.Document.Catalog.GetDictionary(PdfNames.Names)?
                .GetDictionary(PdfNames.EmbeddedFiles) is not { } root)
        {
            return payloads;
        }

        var visited = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<(PdfDictionary Node, int Depth)>();
        pending.Push((root, 0));
        int visitedEntries = 0;
        int specifications = 0;

        while (pending.Count > 0 &&
               visitedEntries <= 4_096 &&
               specifications < context.Validation.AttachmentCandidateLimit)
        {
            (PdfDictionary node, int depth) = pending.Pop();
            if (depth > 64 || !visited.Add(node))
                continue;

            visitedEntries++;
            if (node.GetArray(PdfNames.Names) is { } names)
            {
                for (int i = 1; i < names.Count && visitedEntries <= 4_096; i += 2)
                {
                    visitedEntries++;
                    if (context.Resolve(names.Get(i)).AsDictionary is not { } specification)
                        continue;

                    AddFromSpecification(specification);
                    specifications++;
                    if (specifications >= context.Validation.AttachmentCandidateLimit)
                        break;
                }
            }

            if (node.GetArray(PdfName.Get("Kids")) is not { } kids)
                continue;

            for (int i = 0; i < kids.Count && visitedEntries <= 4_096; i++)
            {
                visitedEntries++;
                if (context.Resolve(kids.Get(i)).AsDictionary is { } child)
                    pending.Push((child, depth + 1));
            }
        }

        return payloads;

        void AddFromSpecification(PdfDictionary specification)
        {
            foreach (PdfARules.EmbeddedFilePayload payload in
                     PdfARules.EnumerateEmbeddedPayloads(specification, reportMalformed: null))
            {
                payloads.Add(payload.Stream);
            }
        }
    }

    private static bool TryReadClaim(
        PdfDocument document,
        PdfAValidationSession validation,
        out PdfAConformance conformance,
        out string? problem)
    {
        conformance = default;
        if (!validation.TryReadXmp(document, out PdfAXmpPacket? xmp, out problem))
        {
            return false;
        }

        XDocument packet = xmp!.Document;
        if (!PdfAXmpStructure.TryReadIdentification(
                packet,
                out PdfAXmpIdentification? identification,
                out problem))
        {
            return false;
        }

        string? partValue = identification!.Part;
        string? levelValue = identification.Conformance?.ToUpperInvariant();

        if (!int.TryParse(partValue, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out int part))
        {
            problem =
                "pdfaid:part is absent from the rdf:Description document subject or is not a " +
                "supported part number.";
            return false;
        }

        string? revision = identification.Revision;
        string supportedRevision = PdfAProfile.SupportedPartFourRevision.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        if (part == 4 && !string.Equals(revision, supportedRevision, StringComparison.Ordinal))
        {
            problem =
                $"pdfaid:rev '{revision ?? "(absent)"}' on the rdf:Description document subject " +
                "is an unsupported PDF/A-4 revision; " +
                $"this validator implements ISO 19005-4:{supportedRevision}.";
            return false;
        }

        PdfAConformance? identified = (part, levelValue) switch
        {
            (1, "A") => PdfAConformance.A1A,
            (1, "B") => PdfAConformance.A1B,
            (2, "A") => PdfAConformance.A2A,
            (2, "B") => PdfAConformance.A2B,
            (2, "U") => PdfAConformance.A2U,
            (3, "A") => PdfAConformance.A3A,
            (3, "B") => PdfAConformance.A3B,
            (3, "U") => PdfAConformance.A3U,
            (4, null or "") => PdfAConformance.A4,
            (4, "E") => PdfAConformance.A4E,
            (4, "F") => PdfAConformance.A4F,
            _ => null,
        };

        if (identified is null)
        {
            problem = $"pdfaid:part '{partValue}' and pdfaid:conformance '{levelValue ?? "(absent)"}' " +
                      "do not identify a supported PDF/A conformance.";
            return false;
        }

        conformance = identified.Value;
        problem = null;
        return true;
    }

    private static bool PartIsAllowed(PdfAContext parent, PdfAConformance embedded) =>
        parent.Part switch
        {
            2 => embedded.Part is 1 or 2,
            4 when !parent.Conformance.AllowsAnyEmbeddedFile => embedded.Part is 1 or 2 or 4,
            _ => true,
        };

    private static bool LooksLikePdf(ReadOnlySpan<byte> file) =>
        file.StartsWith("%PDF-"u8);

    private static string JoinPath(string parent, string name)
    {
        string safeName = new(name
            .Take(160)
            .Select(character => char.IsControl(character) ? '\uFFFD' : character)
            .ToArray());
        if (string.IsNullOrWhiteSpace(safeName))
            safeName = "(unnamed attachment)";

        return string.IsNullOrEmpty(parent) ? safeName : $"{parent} -> {safeName}";
    }

    private static bool IsRecoverableEmbeddedFailure(Exception exception) =>
        exception is not (OutOfMemoryException or StackOverflowException or OperationCanceledException or
            AccessViolationException);

    private static bool TryGetStructuralWarning(
        LoadDiagnostics diagnostics,
        PdfWarning? captured,
        out string? warning)
    {
        if (captured is { } capturedWarning)
        {
            warning = capturedWarning.ToString();
            return true;
        }

        foreach (PdfWarning item in diagnostics)
        {
            if (IsStructuralWarning(item.Code))
            {
                warning = item.ToString();
                return true;
            }
        }

        warning = null;
        return false;
    }

    private static bool IsStructuralWarning(PdfWarningCode code) =>
        code is
            PdfWarningCode.BadStartXref or
            PdfWarningCode.BadXrefSection or
            PdfWarningCode.XrefLoop or
            PdfWarningCode.XrefRebuilt or
            PdfWarningCode.ObjectOffsetMismatch or
            PdfWarningCode.ObjectUnparsable or
            PdfWarningCode.DanglingReference or
            PdfWarningCode.ReferenceCycle or
            PdfWarningCode.StreamLengthMismatch or
            PdfWarningCode.FilterFailed or
            PdfWarningCode.ObjectStreamUnreadable or
            PdfWarningCode.CatalogRecovered or
            PdfWarningCode.PageTreeRecovered or
            PdfWarningCode.PageTreeCycle or
            PdfWarningCode.DecryptionSuspect;
}
