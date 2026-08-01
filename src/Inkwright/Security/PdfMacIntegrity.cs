using Inkwright.Cos;

namespace Inkwright.Security;

/// <summary>Where a document keeps its integrity token (ISO/TS 32004 §6.5).</summary>
public enum PdfMacLocation
{
    /// <summary>The document carries no integrity token.</summary>
    None,

    /// <summary>The token sits in the trailer, in the <c>MAC</c> entry of the authentication dictionary.</summary>
    Standalone,

    /// <summary>The token is an unsigned attribute of a signature, so as not to disturb its coverage.</summary>
    AttachedToSignature,

    /// <summary>The document names a location this library does not recognise.</summary>
    Unknown,
}

/// <summary>
/// What a document says about its own integrity, as ISO/TS 32004 defines it. Encryption in PDF 2.0
/// conceals a document without vouching for it: a reader that decrypts successfully has learned
/// nothing about whether the bytes are the ones the author wrote. The message authentication code
/// this specification adds closes that gap, keyed from the file encryption key, so a valid code
/// proves whoever produced it knew that key.
/// </summary>
public sealed class PdfMacIntegrity
{
    private PdfMacIntegrity(PdfMacLocation location) => Location = location;

    /// <summary>Where the token lives, or that there is none.</summary>
    public PdfMacLocation Location { get; }

    /// <summary>Whether the document carries an integrity token at all.</summary>
    public bool IsPresent => Location is not PdfMacLocation.None;

    /// <summary>Whether encryption permission bit 13 requires a token in the latest revision.</summary>
    public bool IsRequired { get; private init; }

    /// <summary>The stretches of the file the token covers, as pairs of offset and length.</summary>
    public IReadOnlyList<(long Offset, long Length)> Coverage { get; private init; } = [];

    /// <summary>The token itself, which is a cryptographic message syntax structure.</summary>
    public ReadOnlyMemory<byte> Token { get; private init; }

    /// <summary>
    /// Whether the covered stretches account for the whole file bar the token. A token that covers
    /// less has either gone stale — something updated the document and did not renew it — or is
    /// evidence that somebody appended to the file deliberately.
    /// </summary>
    public bool CoversWholeFile { get; private init; }

    /// <summary>Why the coverage is judged the way it is, for a caller reporting to a person.</summary>
    public string? CoverageNote { get; private init; }

    /// <summary>
    /// Whether the code is genuine: it was computed with a key derived from this document's file
    /// encryption key, so whoever wrote it knew the password.
    /// </summary>
    public bool IsAuthentic { get; private init; }

    /// <summary>
    /// Whether the bytes the token covers still hash to what it recorded. A token can be genuine
    /// and yet stale, which is what happens when a processor updates a document and does not renew
    /// the code.
    /// </summary>
    public bool MatchesContent { get; private init; }

    /// <summary>Why verification came out as it did, for a caller reporting to a person.</summary>
    public string? VerificationNote { get; private init; }

    /// <summary>Whether the document's integrity is fully established.</summary>
    public bool IsVerified => IsPresent && IsAuthentic && MatchesContent && CoversWholeFile;

    /// <summary>Reads what a document says about its integrity.</summary>
    /// <param name="document">The document to inspect.</param>
    /// <returns>The integrity information, whose <see cref="IsPresent"/> is false when there is none.</returns>
    public static PdfMacIntegrity Read(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        bool required = document.Reader?.Security?.RequiresMac == true;

        if (document.Resolve(document.Trailer.Get(PdfName.Get("AuthCode"))).AsDictionary is not { } authentication)
        {
            return new PdfMacIntegrity(PdfMacLocation.None)
            {
                IsRequired = required,
                VerificationNote = required
                    ? "Encryption permission bit 13 requires an integrity token, but the AuthCode dictionary is missing."
                    : null,
            };
        }

        PdfMacLocation location = document.Resolve(
            authentication.Get(PdfName.Get("MACLocation"))).AsName?.Value switch
        {
            "Standalone" => PdfMacLocation.Standalone,
            "AttachedToSig" => PdfMacLocation.AttachedToSignature,
            _ => PdfMacLocation.Unknown,
        };

        List<(long Offset, long Length)> coverage = ReadCoverage(document, authentication, location);
        ReadOnlyMemory<byte> file = document.Reader?.FileBytes ?? default;
        (bool whole, string? note) = JudgeCoverage(coverage, file, location);

        byte[] token = ReadToken(document, authentication, location);
        byte[] signatureValue = ReadSignatureValue(document, authentication, location);
        (bool authentic, bool matches, string verification) =
            Verify(document, token, coverage, location, signatureValue);

        return new PdfMacIntegrity(location)
        {
            Coverage = coverage,
            Token = token,
            IsRequired = required,
            CoversWholeFile = whole,
            CoverageNote = note,
            IsAuthentic = authentic,
            MatchesContent = matches,
            VerificationNote = verification,
        };
    }

    /// <summary>
    /// Finds the token. A standalone one sits in the trailer; one attached to a signature is an
    /// unauthenticated attribute of it, so the signature has to be opened to reach it (§6.5).
    /// </summary>
    private static byte[] ReadToken(PdfDocument document, PdfDictionary authentication, PdfMacLocation location)
    {
        if (location != PdfMacLocation.AttachedToSignature)
            return authentication.Get(PdfName.Get("MAC")).AsStringBytes.ToArray();

        if (document.Resolve(authentication.Get(PdfName.Get("SigObjRef"))).AsDictionary is not { } signature)
            return [];

        byte[] contents = signature.Get(PdfNames.Contents).AsStringBytes.ToArray();
        return contents.Length == 0 ? [] : PdfMacSignatureAttribute.Extract(contents);
    }

    private static byte[] ReadSignatureValue(
        PdfDocument document,
        PdfDictionary authentication,
        PdfMacLocation location)
    {
        if (location != PdfMacLocation.AttachedToSignature ||
            document.Resolve(authentication.Get(PdfName.Get("SigObjRef"))).AsDictionary is not { } signature)
        {
            return [];
        }

        byte[] contents = signature.Get(PdfNames.Contents).AsStringBytes.ToArray();
        return contents.Length == 0 ? [] : PdfMacSignatureBuilder.ReadSignatureValue(contents);
    }

    /// <summary>
    /// Runs the two checks the standard's Annex B distinguishes: that the token is genuine, and
    /// that it is about these bytes.
    /// </summary>
    private static (bool Authentic, bool Matches, string Note) Verify(
        PdfDocument document,
        byte[] token,
        List<(long Offset, long Length)> coverage,
        PdfMacLocation location,
        byte[] signatureValue)
    {
        if (token.Length == 0)
            return (false, false, "The document declares integrity protection but carries no token.");

        if (PdfMacToken.Read(token) is not { } parsed)
            return (false, false, "The integrity token is not a structure this library can read.");

        if (document.Reader?.Security is not { } security)
            return (false, false, "The document was opened without its encryption key, so the code cannot be checked.");

        if (document.Resolve(document.Trailer.Get(PdfNames.Encrypt)).AsDictionary is not { } encrypt)
            return (false, false, "The document carries no encryption dictionary to derive a key from.");

        ReadOnlySpan<byte> salt = encrypt.Get(PdfName.Get("KDFSalt")).AsStringBytes;
        if (salt.Length != PdfMacWriter.SaltLength)
            return (false, false, "The encryption dictionary does not carry the required 32-byte KDF salt.");

        bool authentic = parsed.VerifyMac(security.FileKey, salt);
        if (!authentic)
            return (false, false, "The code does not match, so the token was not made with this document's key.");

        byte[]? covered = Collect(document, coverage);
        if (covered is null)
            return (true, false, "The token is genuine but the byte range it names lies outside the file.");

        if (!parsed.IsDigestAvailable)
        {
            return (true, false,
                $"The token is genuine but records a {parsed.Digest.Name} digest, which this machine cannot compute.");
        }

        if (!parsed.VerifyContentDigest())
            return (true, false, "The token code is genuine but its authenticated message digest does not match its payload.");

        bool matches = parsed.VerifyDataDigest(covered);

        if (location == PdfMacLocation.Standalone && parsed.SignatureDigest is not null)
            return (true, false, "A standalone integrity token unexpectedly records a signature digest.");

        if (location == PdfMacLocation.AttachedToSignature)
        {
            if (signatureValue.Length == 0 || parsed.SignatureDigest is null)
                return (true, false, "The attached integrity token cannot be bound to the signature value.");

            if (!parsed.VerifySignatureDigest(signatureValue))
                return (true, false, "The token is genuine but the signature value no longer matches its digest.");
        }

        return (true, matches, matches
            ? "The token is genuine and matches the file."
            : "The token is genuine but records a different digest, so the file changed after it was written.");
    }

    /// <summary>Gathers the bytes a byte range names into one buffer.</summary>
    private static byte[]? Collect(PdfDocument document, List<(long Offset, long Length)> coverage)
    {
        ReadOnlyMemory<byte> file = document.Reader?.FileBytes ?? default;
        if (file.IsEmpty || coverage.Count == 0)
            return null;

        long total = 0;
        foreach ((long offset, long length) in coverage)
        {
            if (offset < 0 || length < 0 || offset + length > file.Length)
                return null;

            total += length;
        }

        byte[] covered = new byte[total];
        int at = 0;

        foreach ((long offset, long length) in coverage)
        {
            file.Span.Slice((int)offset, (int)length).CopyTo(covered.AsSpan(at));
            at += (int)length;
        }

        return covered;
    }

    /// <summary>
    /// Reads the stretches of the file the token covers. A standalone token states its own byte
    /// range; one attached to a signature borrows the signature's, because that is what the
    /// signature already covers (§6.6.2 and §6.6.3).
    /// </summary>
    private static List<(long Offset, long Length)> ReadCoverage(
        PdfDocument document, PdfDictionary authentication, PdfMacLocation location)
    {
        List<(long, long)> coverage = [];

        PdfDictionary source = authentication;

        if (location == PdfMacLocation.AttachedToSignature &&
            document.Resolve(authentication.Get(PdfName.Get("SigObjRef"))).AsDictionary is { } signature)
        {
            source = signature;
        }

        if (document.Resolve(source.Get(PdfName.Get("ByteRange"))).AsArray is not { Count: 4 } range)
            return coverage;

        // The array alternates offset and length, so a pair describes one covered stretch.
        for (int i = 0; i + 1 < range.Count; i += 2)
            coverage.Add((range.Get(i).AsInteger, range.Get(i + 1).AsInteger));

        return coverage;
    }

    private static (bool Whole, string? Note) JudgeCoverage(
        IReadOnlyList<(long Offset, long Length)> coverage,
        ReadOnlyMemory<byte> file,
        PdfMacLocation location)
    {
        if (coverage.Count != 2)
            return (false, "The authentication byte range is not the required pair of covered stretches.");

        if (location is not (PdfMacLocation.Standalone or PdfMacLocation.AttachedToSignature))
            return (false, "The authentication dictionary names an unknown token location.");

        if (file.IsEmpty)
            return (false, "The document was not read from a file, so coverage cannot be judged.");

        (long firstOffset, long firstLength) = coverage[0];
        (long secondOffset, long secondLength) = coverage[1];
        long firstEnd;
        long secondEnd;

        try
        {
            firstEnd = checked(firstOffset + firstLength);
            secondEnd = checked(secondOffset + secondLength);
        }
        catch (OverflowException)
        {
            return (false, "The authentication byte range overflows the file address space.");
        }

        if (firstOffset != 0 || firstLength < 0 || secondLength < 0 ||
            firstEnd >= secondOffset || secondOffset < 0 || secondEnd != file.Length)
        {
            return (false, "The authentication byte range does not cover the file exactly once around one token value.");
        }

        if (firstEnd > int.MaxValue || secondOffset > int.MaxValue ||
            !GapNamesToken(file.Span, (int)firstEnd, (int)secondOffset, location))
        {
            return (false, "The gap in the authentication byte range is not exactly the MAC token value.");
        }

        long covered = firstLength + secondLength;
        return (true, $"The range covers {covered} of {file.Length} bytes, excluding only the token value.");
    }

    private static bool GapNamesToken(ReadOnlySpan<byte> file, int start, int end, PdfMacLocation location)
    {
        if (start < 0 || end <= start || end > file.Length)
            return false;

        ReadOnlySpan<byte> gap = file[start..end];
        bool stringValue = (gap[0] == (byte)'<' && gap[^1] == (byte)'>' &&
                            (gap.Length < 2 || gap[1] != (byte)'<')) ||
                           (gap[0] == (byte)'(' && gap[^1] == (byte)')');
        if (!stringValue)
            return false;

        ReadOnlySpan<byte> key = location == PdfMacLocation.AttachedToSignature
            ? "/Contents"u8
            : "/MAC"u8;

        int at = start - 1;
        while (at >= 0 && IsWhiteSpace(file[at]))
            at--;

        int keyStart = at - key.Length + 1;
        return keyStart >= 0 && file.Slice(keyStart, key.Length).SequenceEqual(key);
    }

    private static bool IsWhiteSpace(byte value) => value is 0 or 9 or 10 or 12 or 13 or 32;
}
