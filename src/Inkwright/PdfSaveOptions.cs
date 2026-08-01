using System.Security.Cryptography.X509Certificates;
using Inkwright.Security;

namespace Inkwright;

/// <summary>Password protection to apply while saving.</summary>
public sealed class PdfEncryptionSettings
{
    /// <summary>The password that opens the document with the permissions below.</summary>
    public string UserPassword { get; init; } = string.Empty;

    /// <summary>The password that grants unrestricted access; defaults to the user password.</summary>
    public string OwnerPassword { get; init; } = string.Empty;

    /// <summary>
    /// The certificates to encrypt for instead of using a password (ISO 32000-2 §7.6.5). When any
    /// are given, the document is opened only by a holder of one of these certificates' private
    /// keys, and the passwords are ignored.
    /// </summary>
    public IReadOnlyList<X509Certificate2> Recipients { get; init; } = [];

    /// <summary>What a reader holding the user password may do.</summary>
    public PdfPermissions Permissions { get; init; } = PdfPermissions.All;

    /// <summary>Whether the metadata stream is encrypted along with everything else.</summary>
    public bool EncryptMetadata { get; init; } = true;

    /// <summary>
    /// Whether to encrypt in Galois/Counter mode, which ISO/TS 32003 adds to PDF 2.0 as the
    /// <c>AESV4</c> method under version 6 and revision 7.
    /// </summary>
    /// <remarks>
    /// The mode authenticates the ciphertext as well as concealing it: a file that has been
    /// tampered with fails to decrypt rather than decrypting to something else. The cost is reach —
    /// a reader that predates the technical specification cannot open the file at all. The default
    /// is therefore the widely readable AES in cipher block chaining mode.
    /// </remarks>
    public bool UseGaloisCounterMode { get; init; }
}

/// <summary>How to write a document out.</summary>
public sealed class PdfSaveOptions
{
    /// <summary>The defaults: compressed object and cross-reference streams, nothing pruned.</summary>
    public static PdfSaveOptions Default { get; } = new();

    /// <summary>Whether to pack small objects into object streams. Raises the version to 1.5.</summary>
    public bool UseObjectStreams { get; init; } = true;

    /// <summary>Whether to deflate the streams the writer generates.</summary>
    public bool Compress { get; init; } = true;

    /// <summary>
    /// Whether to drop objects that nothing reaches from the catalog or the information dictionary.
    /// Off by default, because it forces every object in the file to be parsed and rewritten.
    /// </summary>
    public bool RemoveUnusedObjects { get; init; }

    /// <summary>Encrypt the output with AES-256, or leave it in the clear when absent.</summary>
    public PdfEncryptionSettings? Encryption { get; init; }

    /// <summary>
    /// Whether to add the integrity protection of ISO/TS 32004: a message authentication code over
    /// the whole file, keyed from the file encryption key. Ignored unless the file is encrypted,
    /// because there is no key to derive from otherwise.
    /// </summary>
    /// <remarks>
    /// It answers a question encryption does not. Decrypting successfully says nothing about
    /// whether the bytes are the ones the author wrote; a valid code says whoever produced it knew
    /// the password. Unlike Galois/Counter mode this works with any cipher, and a reader that has
    /// never heard of it ignores the extra dictionary rather than failing to open the file.
    /// </remarks>
    public bool AddIntegrityCode { get; init; }

    /// <summary>Whether to stamp the information dictionary with the current time.</summary>
    public bool UpdateModificationDate { get; init; } = true;

    /// <summary>Whether to name Inkwright as the producer, replacing whatever the source said.</summary>
    public bool SetProducer { get; init; } = true;
}
