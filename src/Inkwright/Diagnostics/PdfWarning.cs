using Inkwright.Cos;

namespace Inkwright.Diagnostics;

/// <summary>
/// What went wrong while reading a file, in a form the caller can act on. Everything recoverable
/// becomes a warning; only structural collapse raises <see cref="PdfFormatException"/>.
/// </summary>
public enum PdfWarningCode
{
    /// <summary>The header did not start with <c>%PDF-</c> at offset zero.</summary>
    HeaderNotAtStart,

    /// <summary>The header version was missing or unparseable.</summary>
    UnknownVersion,

    /// <summary>The <c>startxref</c> pointer was missing, out of range or pointed at nothing.</summary>
    BadStartXref,

    /// <summary>A cross-reference section could not be parsed and was skipped.</summary>
    BadXrefSection,

    /// <summary>The cross-reference chain looped back on itself and was cut.</summary>
    XrefLoop,

    /// <summary>The cross-reference data was unusable, so objects were located by scanning the file.</summary>
    XrefRebuilt,

    /// <summary>An object was not where the cross-reference table said it was.</summary>
    ObjectOffsetMismatch,

    /// <summary>An object could not be parsed and resolved to null.</summary>
    ObjectUnparsable,

    /// <summary>An indirect reference pointed at an object that does not exist.</summary>
    DanglingReference,

    /// <summary>References formed a cycle while resolving.</summary>
    ReferenceCycle,

    /// <summary>A stream declared a <c>Length</c> that did not match where <c>endstream</c> was found.</summary>
    StreamLengthMismatch,

    /// <summary>A stream's filter chain failed part way and produced truncated data.</summary>
    FilterFailed,

    /// <summary>An object stream could not be read, so the objects inside it were lost.</summary>
    ObjectStreamUnreadable,

    /// <summary>The trailer had no <c>Root</c>, so the catalog was located by scanning.</summary>
    CatalogRecovered,

    /// <summary>The page tree was malformed and pages were collected by scanning instead.</summary>
    PageTreeRecovered,

    /// <summary>A page inherited an attribute through a broken parent chain.</summary>
    PageTreeCycle,

    /// <summary>The document is encrypted with a handler the library does not implement.</summary>
    UnsupportedSecurityHandler,

    /// <summary>Decryption produced data that failed its own integrity check.</summary>
    DecryptionSuspect,

    /// <summary>A font program could not be parsed, so metrics fell back to defaults.</summary>
    FontUnreadable,

    /// <summary>A content stream ended in the middle of an operator.</summary>
    ContentTruncated,

    /// <summary>A content stream contained an operator the library does not know.</summary>
    UnknownOperator,
}

/// <summary>
/// One recoverable problem found while reading a file.
/// </summary>
/// <param name="Code">What kind of problem it was.</param>
/// <param name="Message">A human-readable description.</param>
/// <param name="ObjectId">The object the problem concerns, when known.</param>
/// <param name="Offset">The byte offset in the file, when known.</param>
public readonly record struct PdfWarning(
    PdfWarningCode Code,
    string Message,
    PdfObjectId ObjectId = default,
    long Offset = -1)
{
    /// <inheritdoc />
    public override string ToString()
    {
        string where = ObjectId.IsValid ? $" at {ObjectId}" : Offset >= 0 ? $" at byte {Offset}" : string.Empty;
        return $"{Code}{where}: {Message}";
    }
}
