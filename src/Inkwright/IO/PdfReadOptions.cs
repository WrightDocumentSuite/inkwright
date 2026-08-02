using System.Security.Cryptography.X509Certificates;
using Inkwright.Cos;
using Inkwright.Diagnostics;

namespace Inkwright.IO;

/// <summary>How to read a file: what password to try, and how much damage to tolerate.</summary>
public sealed class PdfReadOptions
{
    /// <summary>The default options: empty password, recovery enabled, warnings collected.</summary>
    public static PdfReadOptions Default { get; } = new();

    /// <summary>
    /// The password to open the document with. The empty string is correct for the many files that
    /// are encrypted with an owner password only.
    /// </summary>
    public string Password { get; init; } = string.Empty;

    /// <summary>
    /// The certificate to open a public-key encrypted document with (ISO 32000-2 §7.6.5). It must
    /// carry its private key; only a recipient the document was encrypted for can unwrap the key.
    /// </summary>
    public X509Certificate2? DecryptionCertificate { get; init; }

    /// <summary>
    /// Whether to fall back on scanning the file for objects when the cross-reference data is
    /// unusable. Turning this off also rejects direct trailer <c>Root</c>, catalog <c>Pages</c>, and
    /// page-tree <c>Kids</c> objects instead of normalizing them, and makes damaged files raise
    /// <see cref="PdfFormatException"/>.
    /// </summary>
    public bool Recover { get; init; } = true;

    /// <summary>
    /// Whether to carry on with an encrypted file that would not open, instead of raising
    /// <see cref="PdfPasswordException"/>. The document is then readable only as ciphertext: its
    /// structure is there but every string and stream is meaningless. Useful for a tool that sorts
    /// files by whether they are locked; wrong for anything that reads content.
    /// </summary>
    public bool OpenLockedDocuments { get; init; }

    /// <summary>Called for every warning as it is raised, in addition to collecting it.</summary>
    public Action<PdfWarning>? OnWarning { get; init; }

    /// <summary>How many warnings to retain before only counting them.</summary>
    public int MaxWarnings { get; init; } = 1000;

    /// <summary>
    /// Internal fail-closed decoding hook used when recursively opening an untrusted embedded PDF.
    /// Normal public reads leave it unset and keep the tolerant decoder.
    /// </summary>
    internal IPdfStreamDecodePolicy? StreamDecodePolicy { get; init; }
}
