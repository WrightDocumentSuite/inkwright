using System.Security.Cryptography;
using Inkwright.Cos;

namespace Inkwright.Security;

/// <summary>
/// Builds the integrity code that rides along with a signature (ISO/TS 32004 §6.5.2). It records
/// two digests: one of the document, and one of the signature itself, so the code can be checked
/// whatever the signature's own validation comes to.
/// </summary>
internal static class PdfMacSigner
{
    /// <summary>
    /// Checks that the encryption dictionary already carries the salt the key derivation needs.
    /// </summary>
    /// <param name="document">The document to check.</param>
    /// <exception cref="InvalidOperationException">The document was not written with a salt.</exception>
    /// <remarks>
    /// The salt is meant to stay the same through every revision of a document (ISO/TS 32004
    /// §5.1.1), so it belongs to the document from the moment it is encrypted. Adding one now is
    /// not an option either: the encryption dictionary is the one object written in the clear, and
    /// rewriting it in an appended revision would encrypt the very entries a reader needs to derive
    /// the key.
    /// </remarks>
    internal static void RequireSalt(PdfDocument document)
    {
        if (document.Resolve(document.Trailer.Get(PdfNames.Encrypt)).AsDictionary is { } encrypt &&
            encrypt.Get(PdfName.Get("KDFSalt")).AsStringBytes.Length == PdfMacWriter.SaltLength)
        {
            return;
        }

        throw new InvalidOperationException(
            "The document carries no key derivation salt, so no integrity code can be keyed to it. " +
            $"Save it with {nameof(PdfSaveOptions)}.{nameof(PdfSaveOptions.AddIntegrityCode)} set before signing.");
    }

    /// <summary>Attaches a code to a freshly built signature.</summary>
    /// <param name="signature">The signature bytes.</param>
    /// <param name="covered">The document bytes the signature covers.</param>
    /// <param name="document">The document, for its encryption key and salt.</param>
    /// <returns>The signature with the code attached.</returns>
    internal static byte[] Attach(byte[] signature, ReadOnlySpan<byte> covered, PdfDocument document)
    {
        if (document.Reader?.Security is not { } security)
            throw new InvalidOperationException("The document has no active encryption key for the integrity code.");

        if (document.Resolve(document.Trailer.Get(PdfNames.Encrypt)).AsDictionary is not { } encrypt)
            throw new InvalidOperationException("The document has no encryption dictionary for the integrity code.");

        byte[] salt = encrypt.Get(PdfName.Get("KDFSalt")).AsStringBytes.ToArray();
        if (salt.Length != PdfMacWriter.SaltLength)
            throw new InvalidOperationException("The document has no valid 32-byte salt for the integrity code.");

        byte[] signatureValue = PdfMacSignatureBuilder.ReadSignatureValue(signature);
        if (signatureValue.Length == 0)
            throw new InvalidOperationException("The CMS signature contains no signature value for the integrity code.");

        byte[] token = PdfMacWriter.Build(
            security.FileKey, salt, SHA256.HashData(covered), SHA256.HashData(signatureValue));

        return PdfMacSignatureBuilder.Attach(signature, token);
    }
}
