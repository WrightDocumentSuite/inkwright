using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using Inkwright.Cos;

#pragma warning disable CA5350, CA5351 // Legacy algorithms are mandated by the file format, not chosen.

namespace Inkwright.Security;

/// <summary>
/// The public-key security handler of ISO 32000-2 §7.6.5. Instead of a password, access is granted
/// to the holders of named X.509 certificates: the file encryption key is wrapped for each
/// recipient inside a CMS envelope, and only a recipient's private key unwraps it. Everything is
/// built on <see cref="EnvelopedCms"/> from the base class library, so no third-party cryptography
/// stack is involved. Reading accepts every AES variant a producer might have written; writing
/// produces 256-bit AES with crypt filters (<c>adbe.pkcs7.s5</c>), because a weaker scheme would
/// only give a false sense of protection.
/// </summary>
internal static class PublicKeySecurityHandler
{
    /// <summary>The handler names a public-key encrypted file is likely to carry.</summary>
    private static readonly HashSet<string> HandlerNames =
        new(StringComparer.Ordinal) { "Adobe.PubSec", "Adobe.PPKLite", "Entrust.PPKEF", "Adobe.PPKMS" };

    /// <summary>Whether an encryption dictionary asks for a public-key handler rather than the standard one.</summary>
    internal static bool Handles(PdfDictionary encrypt)
    {
        if (encrypt.GetName(PdfNames.SubFilter)?.Value is { } subFilter && subFilter.StartsWith("adbe.pkcs7.s", StringComparison.Ordinal))
            return true;

        // A crypt filter can carry the sub-filter instead, when the top-level dictionary does not.
        if (encrypt.GetDictionary(PdfNames.CF) is { } filters)
        {
            foreach (KeyValuePair<PdfName, PdfValue> entry in filters)
            {
                if (entry.Value.AsDictionary?.GetName(PdfNames.SubFilter)?.Value?.StartsWith("adbe.pkcs7.s", StringComparison.Ordinal) == true)
                    return true;
            }
        }

        PdfName? filter = encrypt.GetName(PdfNames.Filter);
        return filter is not null && HandlerNames.Contains(filter.Value) && !encrypt.ContainsKey(PdfNames.U);
    }

    /// <summary>Opens a public-key encrypted document with the recipient's certificate.</summary>
    /// <param name="encrypt">The <c>Encrypt</c> dictionary.</param>
    /// <param name="certificate">The recipient certificate, which must carry its private key.</param>
    /// <param name="handler">Receives the handler when the certificate unwraps the key.</param>
    /// <param name="failure">Receives why the document could not be opened.</param>
    internal static bool TryOpen(
        PdfDictionary encrypt,
        X509Certificate2? certificate,
        out StandardSecurityHandler? handler,
        out string? failure)
    {
        handler = null;
        failure = null;

        if (certificate is null)
        {
            failure = "The document is encrypted for named recipients; a decryption certificate is needed to open it.";
            return false;
        }

        if (!certificate.HasPrivateKey)
        {
            failure = "The decryption certificate has no private key.";
            return false;
        }

        int version = (int)encrypt.GetInteger(PdfNames.V, 0);
        PdfDictionary? streamFilter = ReferencedFilter(encrypt, PdfNames.StmF, version);
        PdfDictionary? stringFilter = ReferencedFilter(encrypt, PdfNames.StrF, version);
        PdfDictionary? embeddedFileFilter = ReferencedFilter(encrypt, PdfNames.EFF, version)
            ?? streamFilter;
        bool encryptMetadata = streamFilter?.GetBoolean(PdfNames.EncryptMetadata, true)
            ?? encrypt.GetBoolean(PdfNames.EncryptMetadata, true);
        (CryptMethod streams, CryptMethod strings, CryptMethod embeddedFiles, int keyBits,
            IReadOnlyDictionary<PdfName, CryptMethod> cryptFilters) = ResolveMethods(encrypt, version);

        List<byte[]> recipients = ReadRecipients(
            encrypt, version, streamFilter, stringFilter, embeddedFileFilter, out failure);
        if (failure is not null)
            return false;

        if (recipients.Count == 0)
        {
            failure = "The public-key encryption dictionary lists no recipients.";
            return false;
        }

        byte[]? recipientContent = Unwrap(recipients, certificate);
        if (recipientContent is null)
        {
            failure = "None of the document's recipient lists could be opened with the certificate.";
            return false;
        }

        if (recipientContent.Length < 24)
        {
            failure = "The matching document recipient envelope has no permission word.";
            return false;
        }

        int permissionBits = BinaryPrimitives.ReadInt32BigEndian(recipientContent.AsSpan(20, 4));
        bool ownerAccess = (permissionBits & 0x02) != 0;
        byte[] fileKey = DeriveFileKey(recipientContent, recipients, keyBits, encryptMetadata);

        // Revision does not appear in a public-key dictionary; the crypt method alone decides the
        // object-key derivation, so any value that is not a legacy standard-handler revision does.
        handler = StandardSecurityHandler.FromFileKey(
            fileKey, streams, strings, encryptMetadata, revision: 6, ownerAccess, permissionBits,
            embeddedFiles, cryptFilters);
        return true;
    }

    /// <summary>
    /// Encrypts a document for a set of recipients and returns the handler plus the dictionary to
    /// write. The key is 256-bit AES with crypt filters, the interoperable modern default.
    /// </summary>
    /// <param name="recipients">The certificates whose holders may open the document.</param>
    /// <param name="permissions">The operations a recipient is allowed.</param>
    /// <param name="encryptMetadata">Whether the metadata stream is encrypted too.</param>
    /// <param name="dictionary">Receives the <c>Encrypt</c> dictionary to write.</param>
    /// <param name="requireMac">Whether permission bit 13 shall require an ISO/TS 32004 token.</param>
    internal static StandardSecurityHandler Create(
        IReadOnlyList<X509Certificate2> recipients,
        PdfPermissions permissions,
        bool encryptMetadata,
        out PdfDictionary dictionary,
        bool requireMac = false)
    {
        if (recipients.Count == 0)
            throw new ArgumentException("Public-key encryption needs at least one recipient certificate.", nameof(recipients));

        byte[] seed = RandomNumberGenerator.GetBytes(20);
        // Bit 10 is deprecated in PDF 2.0 but public-key writers are required to keep it set for
        // compatibility with older readers. Bit 2 remains clear: recipients get the requested
        // rights, not owner-level permission to change the encryption itself.
        int permissionBits = unchecked((int)0xFFFFF0C0) | (1 << 9) | (int)permissions;
        if (requireMac)
            permissionBits &= ~(1 << 12);

        // The enveloped content is the 20-byte seed followed by the 4-byte permission word, most
        // significant byte first (§7.6.5.3).
        byte[] content =
        [
            .. seed,
            (byte)(permissionBits >> 24), (byte)(permissionBits >> 16), (byte)(permissionBits >> 8), (byte)permissionBits,
        ];

        byte[] recipientCms = BuildEnvelope(content, recipients);
        byte[] fileKey = DeriveFileKey(seed, [recipientCms], keyBits: 256, encryptMetadata);

        var cryptFilter = new PdfDictionary
        {
            [PdfNames.CFM] = PdfValue.Name(PdfNames.AESV3),
            [PdfNames.SubFilter] = PdfValue.Name(PdfName.Get("adbe.pkcs7.s5")),
            [PdfName.Get("AuthEvent")] = PdfValue.Name(PdfName.Get("DocOpen")),
            [PdfName.Get("Length")] = PdfValue.Integer(256),
            [PdfName.Get("Recipients")] = PdfValue.Array(new PdfArray(PdfValue.String(recipientCms))),
        };

        if (!encryptMetadata)
            cryptFilter.Set(PdfNames.EncryptMetadata, PdfValue.False);

        dictionary = new PdfDictionary
        {
            [PdfNames.Filter] = PdfValue.Name(PdfName.Get("Adobe.PubSec")),
            [PdfNames.SubFilter] = PdfValue.Name(PdfName.Get("adbe.pkcs7.s5")),
            [PdfNames.V] = PdfValue.Integer(5),
            [PdfName.Get("Length")] = PdfValue.Integer(256),
            [PdfNames.CF] = PdfValue.Dictionary(new PdfDictionary { [PdfNames.DefaultCryptFilter] = PdfValue.Dictionary(cryptFilter) }),
            [PdfNames.StmF] = PdfValue.Name(PdfNames.DefaultCryptFilter),
            [PdfNames.StrF] = PdfValue.Name(PdfNames.DefaultCryptFilter),
        };

        return StandardSecurityHandler.FromFileKey(
            fileKey,
            CryptMethod.AesV3,
            CryptMethod.AesV3,
            encryptMetadata,
            revision: 6,
            ownerAccess: false,
            permissionBits,
            embeddedFileMethod: CryptMethod.AesV3,
            cryptFilters: new Dictionary<PdfName, CryptMethod>
            {
                [PdfNames.DefaultCryptFilter] = CryptMethod.AesV3,
            });
    }

    /// <summary>
    /// Wraps the keying material in a CMS <c>EnvelopedData</c> for every recipient, with the
    /// content itself under 256-bit AES (§7.6.5.3). One envelope carries the whole recipient list.
    /// </summary>
    private static byte[] BuildEnvelope(byte[] content, IReadOnlyList<X509Certificate2> recipients)
    {
        var envelope = new EnvelopedCms(new ContentInfo(content), new AlgorithmIdentifier(new Oid("2.16.840.1.101.3.4.1.42")));
        var collection = new CmsRecipientCollection();

        foreach (X509Certificate2 recipient in recipients)
            collection.Add(new CmsRecipient(SubjectIdentifierType.IssuerAndSerialNumber, recipient));

        envelope.Encrypt(collection);
        return envelope.Encode();
    }

    private static byte[]? Unwrap(List<byte[]> recipients, X509Certificate2 certificate)
    {
        var store = new X509Certificate2Collection(certificate);

        foreach (byte[] recipientCms in recipients)
        {
            try
            {
                var envelope = new EnvelopedCms();
                envelope.Decode(recipientCms);
                envelope.Decrypt(store);

                byte[] content = envelope.ContentInfo.Content;
                if (content.Length >= 20)
                    return content;
            }
            catch (CryptographicException)
            {
                // This list was for other recipients, or the certificate cannot open it; try the next.
            }
        }

        return null;
    }

    /// <summary>
    /// Derives the file encryption key of §7.6.5.3: a digest over the seed, then every recipient
    /// CMS object in order, then a four-byte marker when metadata is left in the clear. The digest
    /// is SHA-256 for a 256-bit key and SHA-1 for the legacy 128-bit one.
    /// </summary>
    private static byte[] DeriveFileKey(byte[] content, IReadOnlyList<byte[]> recipients, int keyBits, bool encryptMetadata)
    {
        byte[] seed = content.Length > 20 ? content[..20] : content;

        using IncrementalHash hash = IncrementalHash.CreateHash(
            keyBits >= 256 ? HashAlgorithmName.SHA256 : HashAlgorithmName.SHA1);

        hash.AppendData(seed);
        foreach (byte[] recipient in recipients)
            hash.AppendData(recipient);

        if (!encryptMetadata)
            hash.AppendData([0xFF, 0xFF, 0xFF, 0xFF]);

        byte[] digest = hash.GetHashAndReset();
        return digest[..(keyBits / 8)];
    }

    private static List<byte[]> ReadRecipients(
        PdfDictionary encrypt,
        int version,
        PdfDictionary? streamFilter,
        PdfDictionary? stringFilter,
        PdfDictionary? embeddedFileFilter,
        out string? failure)
    {
        failure = null;

        // With crypt filters (V >= 4) the recipient list lives in the crypt filter dictionary;
        // without them it is a top-level Recipients entry.
        if (version >= 4)
        {
            List<byte[]> streamRecipients = streamFilter is null ? [] : Collect(streamFilter);
            List<byte[]> stringRecipients = stringFilter is null ? [] : Collect(stringFilter);
            List<byte[]> embeddedFileRecipients = embeddedFileFilter is null ? [] : Collect(embeddedFileFilter);

            if (streamRecipients.Count > 0 && stringRecipients.Count > 0 &&
                !SameRecipients(streamRecipients, stringRecipients))
            {
                failure = "Different default stream and string recipient lists are not supported.";
                return [];
            }

            List<byte[]> defaultRecipients = streamRecipients.Count > 0
                ? streamRecipients
                : stringRecipients;
            if (defaultRecipients.Count > 0 && embeddedFileRecipients.Count > 0 &&
                !SameRecipients(defaultRecipients, embeddedFileRecipients))
            {
                failure = "Different default and embedded-file recipient lists are not supported.";
                return [];
            }

            if (streamRecipients.Count > 0)
                return streamRecipients;

            if (stringRecipients.Count > 0)
                return stringRecipients;

            if (embeddedFileRecipients.Count > 0)
                return embeddedFileRecipients;
        }

        return Collect(encrypt);
    }

    private static List<byte[]> Collect(PdfDictionary dictionary)
    {
        List<byte[]> recipients = [];
        PdfValue value = dictionary.Get(PdfName.Get("Recipients"));

        if (value.AsArray is { } array)
        {
            for (int i = 0; i < array.Count; i++)
            {
                ReadOnlySpan<byte> bytes = array[i].AsStringBytes;
                if (!bytes.IsEmpty)
                    recipients.Add(bytes.ToArray());
            }
        }
        else if (value.AsStringBytes is { IsEmpty: false } single)
        {
            recipients.Add(single.ToArray());
        }

        return recipients;
    }

    private static PdfDictionary? ReferencedFilter(PdfDictionary encrypt, PdfName entry, int version)
    {
        if (version < 4 || encrypt.GetName(entry) is not { } name || name.Value == "Identity")
            return null;

        return encrypt.GetDictionary(PdfNames.CF)?.GetDictionary(name);
    }

    private static bool SameRecipients(IReadOnlyList<byte[]> left, IReadOnlyList<byte[]> right)
    {
        if (left.Count != right.Count)
            return false;

        for (int i = 0; i < left.Count; i++)
        {
            if (!left[i].AsSpan().SequenceEqual(right[i]))
                return false;
        }

        return true;
    }

    private static (
        CryptMethod Streams,
        CryptMethod Strings,
        CryptMethod EmbeddedFiles,
        int KeyBits,
        IReadOnlyDictionary<PdfName, CryptMethod> Filters) ResolveMethods(PdfDictionary encrypt, int version)
    {
        if (version < 4)
        {
            int keyLength = (int)encrypt.GetInteger(PdfName.Get("Length"), 40);
            return (CryptMethod.Rc4, CryptMethod.Rc4, CryptMethod.Rc4,
                keyLength >= 128 ? 128 : 128, new Dictionary<PdfName, CryptMethod>());
        }

        PdfDictionary? filters = encrypt.GetDictionary(PdfNames.CF);
        var methods = new Dictionary<PdfName, CryptMethod>();
        var keyLengths = new Dictionary<PdfName, int>();

        if (filters is not null)
        {
            foreach (PdfName name in filters.Keys)
            {
                PdfDictionary? definition = filters.Get(name).AsDictionary;
                (CryptMethod method, int bits) = definition?.GetName(PdfNames.CFM)?.Value switch
                {
                    "V2" => (CryptMethod.Rc4, 128),
                    "AESV2" => (CryptMethod.AesV2, 128),
                    "AESV3" => (CryptMethod.AesV3, 256),
                    "AESV4" => (CryptMethod.AesGcm, 256),
                    "None" => (CryptMethod.None, 128),
                    _ => (CryptMethod.AesV3, 256),
                };
                methods[name] = method;
                keyLengths[name] = bits;
            }
        }

        (CryptMethod Method, int KeyBits) Lookup(PdfName? filterName)
        {
            if (filterName is null || filterName.Value == "Identity")
                return (CryptMethod.None, 128);

            return methods.TryGetValue(filterName, out CryptMethod method)
                ? (method, keyLengths[filterName])
                : (CryptMethod.AesV3, 256);
        }

        PdfName streamFilter = encrypt.GetName(PdfNames.StmF) ?? PdfNames.Identity;
        (CryptMethod streams, int streamBits) = Lookup(streamFilter);
        (CryptMethod strings, int stringBits) = Lookup(encrypt.GetName(PdfNames.StrF) ?? PdfNames.Identity);
        (CryptMethod embeddedFiles, int embeddedBits) = Lookup(encrypt.GetName(PdfNames.EFF) ?? streamFilter);
        return (streams, strings, embeddedFiles,
            Math.Max(streamBits, Math.Max(stringBits, embeddedBits)), methods);
    }
}
