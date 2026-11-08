using System.Collections;
using Inkwright.Cos;

namespace Inkwright.Diagnostics;

/// <summary>
/// Everything the reader had to work around while loading a document. An empty collection means
/// the file was clean; anything in it is a fact about the source file, not about the model.
/// </summary>
public sealed class LoadDiagnostics : IReadOnlyList<PdfWarning>
{
    private readonly List<PdfWarning> _warnings = [];
    private readonly int _limit;
    private Action<PdfWarning>? _sink;

    /// <summary>Creates a diagnostics collection.</summary>
    /// <param name="limit">The most warnings to retain; further ones only bump <see cref="Suppressed"/>.</param>
    public LoadDiagnostics(int limit = 1000)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        _limit = limit;
    }

    /// <summary>The number of warnings dropped after the retention limit was reached.</summary>
    public int Suppressed { get; private set; }

    /// <inheritdoc />
    public int Count => _warnings.Count;

    /// <summary>Whether anything at all went wrong.</summary>
    public bool IsClean => _warnings.Count == 0 && Suppressed == 0;

    /// <inheritdoc />
    public PdfWarning this[int index] => _warnings[index];

    internal void SetSink(Action<PdfWarning>? sink) => _sink = sink;

    internal void Add(PdfWarning warning)
    {
        if (_warnings.Count < _limit)
            _warnings.Add(warning);
        else
            Suppressed++;

        _sink?.Invoke(warning);
    }

    internal void Add(PdfWarningCode code, string message, PdfObjectId objectId = default, long offset = -1) =>
        Add(new PdfWarning(code, message, objectId, offset));

    /// <summary>Whether any warning carries the given code.</summary>
    /// <param name="code">The code to look for.</param>
    public bool Contains(PdfWarningCode code)
    {
        foreach (PdfWarning warning in _warnings)
        {
            if (warning.Code == code)
                return true;
        }

        return false;
    }

    /// <inheritdoc />
    public IEnumerator<PdfWarning> GetEnumerator() => _warnings.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <inheritdoc />
    public override string ToString() =>
        Suppressed == 0 ? $"{_warnings.Count} warnings" : $"{_warnings.Count} warnings (+{Suppressed} suppressed)";
}

/// <summary>
/// Raised when an encrypted file will not open with the password it was given. The alternative — an
/// apparently valid document whose every string and stream is ciphertext — reads as a file with no
/// text in it, and a caller processing a batch would never notice.
/// </summary>
public sealed class PdfPasswordException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">Why the document stayed shut.</param>
    /// <param name="wasPasswordSupplied">Whether the caller gave a password at all.</param>
    public PdfPasswordException(string message, bool wasPasswordSupplied = false) : base(message) =>
        WasPasswordSupplied = wasPasswordSupplied;

    /// <summary>
    /// Whether a password was tried. When false, the file simply needs one; when true, the one
    /// supplied was wrong.
    /// </summary>
    public bool WasPasswordSupplied { get; }
}

/// <summary>
/// Raised when an authenticated cipher reports that the bytes it was given are not the bytes that
/// were encrypted. Only Galois/Counter mode, which ISO/TS 32003 adds, can tell: it computes a tag
/// over the ciphertext, and a tag that does not match means the data was altered after encryption.
/// </summary>
/// <remarks>
/// Swallowing this and returning nothing would leave the caller with a document whose pages appear
/// blank, indistinguishable from a document that really is blank. Choosing an authenticated cipher
/// is a request to be told about exactly this, so being told is what happens.
/// </remarks>
public sealed class PdfTamperedException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">What failed to authenticate.</param>
    /// <param name="innerException">The failure the cipher reported.</param>
    public PdfTamperedException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Raised when a file cannot be read at all: no header, no objects, or a catalog that cannot be
/// found by any means. Recoverable damage never reaches this exception.
/// </summary>
public sealed class PdfFormatException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">What made the file unreadable.</param>
    public PdfFormatException(string message) : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">What made the file unreadable.</param>
    /// <param name="innerException">The underlying failure.</param>
    public PdfFormatException(string message, Exception innerException) : base(message, innerException)
    {
    }

    /// <summary>Creates the exception.</summary>
    public PdfFormatException() : base("The PDF file could not be read.")
    {
    }

    /// <summary>The byte offset the failure was noticed at, or -1 when it is not tied to one.</summary>
    public long Offset { get; init; } = -1;

    /// <summary>The object being read when the failure happened, when there was one.</summary>
    public PdfObjectId ObjectId { get; init; }
}

/// <summary>
/// Raised when reading or lazily decoding a PDF would exceed a limit selected by the caller.
/// This is distinct from malformed-file recovery so callers can reject, quarantine, or retry the
/// document with a deliberately larger budget.
/// </summary>
public sealed class PdfResourceLimitException : Exception
{
    /// <summary>Creates an exception describing the exhausted resource budget.</summary>
    /// <param name="limitName">
    /// The configured limit identifier that was exceeded, normally a
    /// <see cref="IO.PdfResourceLimits"/> property name. The legacy process-wide ceiling is
    /// reported as <c>PdfFilterLimits.MaxDecodedBytes</c>.
    /// </param>
    /// <param name="limit">The configured maximum.</param>
    /// <param name="observed">The amount requested or observed at the boundary.</param>
    public PdfResourceLimitException(string limitName, long limit, long observed)
        : base($"The PDF exceeded {limitName}: limit {limit}, observed or requested {observed}.")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(limitName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        LimitName = limitName;
        Limit = limit;
        Observed = observed;
    }

    /// <summary>
    /// The configured limit identifier that was exceeded, such as a
    /// <see cref="IO.PdfResourceLimits"/> property or <c>PdfFilterLimits.MaxDecodedBytes</c>.
    /// </summary>
    public string LimitName { get; }

    /// <summary>The configured maximum.</summary>
    public long Limit { get; }

    /// <summary>The amount requested or observed at the boundary.</summary>
    public long Observed { get; }
}
