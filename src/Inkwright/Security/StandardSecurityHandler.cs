using System.Security.Cryptography;
using System.Text;
using Inkwright.Cos;

#pragma warning disable CA5350, CA5351 // Legacy algorithms are mandated by the file format, not chosen.

namespace Inkwright.Security;

/// <summary>How a crypt filter transforms the bytes it covers.</summary>
internal enum CryptMethod
{
    None,
    Rc4,
    AesV2,
    AesV3,

    /// <summary>AES in Galois/Counter mode, added by ISO 32000-2 as <c>AESV4</c>.</summary>
    AesGcm,
}

/// <summary>
/// The standard security handler of ISO 32000-1 §7.6.3 and §7.6.4, covering revisions 2 through 6,
/// and the revision 7 that ISO/TS 32003 adds for Galois/Counter mode. Reading supports RC4 and
/// every AES variant; writing produces revision 6 or 7, because emitting a weaker scheme would give
/// callers a false sense of protection.
/// </summary>
internal sealed class StandardSecurityHandler
{
    private static ReadOnlySpan<byte> PaddingBytes =>
    [
        0x28, 0xBF, 0x4E, 0x5E, 0x4E, 0x75, 0x8A, 0x41, 0x64, 0x00, 0x4E, 0x56, 0xFF, 0xFA, 0x01, 0x08,
        0x2E, 0x2E, 0x00, 0xB6, 0xD0, 0x68, 0x3E, 0x80, 0x2F, 0x0C, 0xA9, 0xFE, 0x64, 0x53, 0x69, 0x7A,
    ];

    private readonly byte[] _fileKey;
    private readonly IReadOnlyDictionary<PdfName, CryptMethod> _cryptFilters;

    private StandardSecurityHandler(
        byte[] fileKey,
        int revision,
        CryptMethod streamMethod,
        CryptMethod stringMethod,
        CryptMethod embeddedFileMethod,
        IReadOnlyDictionary<PdfName, CryptMethod>? cryptFilters,
        bool ownerAccess,
        int permissionBits,
        bool encryptMetadata)
    {
        _fileKey = fileKey;
        Revision = revision;
        StreamMethod = streamMethod;
        StringMethod = stringMethod;
        EmbeddedFileMethod = embeddedFileMethod;
        _cryptFilters = cryptFilters ?? new Dictionary<PdfName, CryptMethod>();
        HasOwnerAccess = ownerAccess;

        // The bit field has two reserved bits set and every bit above the twelfth, so handing it
        // over raw would report a permission set of -3900 rather than the rights it grants.
        Permissions = ownerAccess ? PdfPermissions.All : (PdfPermissions)permissionBits & PdfPermissions.All;
        RequiresMac = (permissionBits & (1 << 12)) == 0;
        EncryptMetadata = encryptMetadata;
    }

    /// <summary>
    /// Builds a handler around a file key worked out some other way — by a public-key security
    /// handler, whose key comes from a CMS envelope rather than a password. The crypt filter
    /// machinery below is identical either way; only where the key came from differs.
    /// </summary>
    internal static StandardSecurityHandler FromFileKey(
        byte[] fileKey,
        CryptMethod streamMethod,
        CryptMethod stringMethod,
        bool encryptMetadata,
        int revision,
        bool ownerAccess,
        int permissionBits,
        CryptMethod? embeddedFileMethod = null,
        IReadOnlyDictionary<PdfName, CryptMethod>? cryptFilters = null) =>
        new(fileKey, revision, streamMethod, stringMethod, embeddedFileMethod ?? streamMethod,
            cryptFilters, ownerAccess, permissionBits, encryptMetadata);

    internal int Revision { get; }

    internal CryptMethod StreamMethod { get; }

    internal CryptMethod StringMethod { get; }

    internal CryptMethod EmbeddedFileMethod { get; }

    internal bool HasOwnerAccess { get; }

    internal PdfPermissions Permissions { get; }

    /// <summary>Whether permission bit 13 requires an ISO/TS 32004 authentication code.</summary>
    internal bool RequiresMac { get; }

    internal bool EncryptMetadata { get; }

    /// <summary>
    /// The file encryption key. Integrity protection derives its own key from this one
    /// (ISO/TS 32004 §6.4), which is what ties a valid code to knowledge of the password.
    /// </summary>
    internal ReadOnlySpan<byte> FileKey => _fileKey;

    /// <summary>
    /// Opens an encrypted document.
    /// </summary>
    /// <param name="encrypt">The <c>Encrypt</c> dictionary.</param>
    /// <param name="firstId">The first element of the file <c>ID</c>, empty when absent.</param>
    /// <param name="password">The password to try; the empty string covers most encrypted files.</param>
    /// <param name="handler">Receives the handler when the password is accepted.</param>
    /// <param name="failure">Receives why the document could not be opened.</param>
    internal static bool TryOpen(
        PdfDictionary encrypt,
        ReadOnlySpan<byte> firstId,
        string password,
        out StandardSecurityHandler? handler,
        out string? failure)
    {
        handler = null;
        failure = null;

        PdfName? filter = encrypt.GetName(PdfNames.Filter);
        if (filter is not null && filter.Value != "Standard")
        {
            failure = $"Security handler /{filter.Value} is not supported.";
            return false;
        }

        int version = (int)encrypt.GetInteger(PdfNames.V, 0);
        int revision = (int)encrypt.GetInteger(PdfNames.R, version >= 4 ? 4 : 2);
        int keyLength = (int)encrypt.GetInteger(PdfName.Get("Length"), 40) / 8;
        if (keyLength is < 5 or > 32)
            keyLength = version == 1 ? 5 : 16;

        byte[] ownerEntry = encrypt.Get(PdfNames.O).AsStringBytes.ToArray();
        byte[] userEntry = encrypt.Get(PdfNames.U).AsStringBytes.ToArray();
        int permissionBits = (int)encrypt.GetInteger(PdfNames.P, -1);
        bool encryptMetadata = encrypt.GetBoolean(PdfNames.EncryptMetadata, true);

        (CryptMethod streams, CryptMethod strings, CryptMethod embeddedFiles,
            IReadOnlyDictionary<PdfName, CryptMethod> cryptFilters) = ResolveMethods(encrypt, version);

        if (revision >= 5)
        {
            return TryOpenRevision5(
                encrypt, password, revision, ownerEntry, userEntry, permissionBits, encryptMetadata,
                streams, strings, embeddedFiles, cryptFilters, out handler, out failure);
        }

        byte[] passwordBytes = EncodeLegacyPassword(password);

        bool ownerAccess = false;
        byte[]? key = null;

        if (ownerEntry.Length >= 32)
        {
            byte[] recoveredUser = RecoverUserPassword(passwordBytes, ownerEntry, revision, keyLength);
            byte[] candidate = ComputeLegacyKey(
                recoveredUser, ownerEntry, permissionBits, firstId, revision, keyLength, encryptMetadata);

            if (VerifyUserPassword(candidate, userEntry, firstId, revision))
            {
                key = candidate;
                ownerAccess = true;
            }
        }

        if (key is null)
        {
            byte[] candidate = ComputeLegacyKey(
                passwordBytes, ownerEntry, permissionBits, firstId, revision, keyLength, encryptMetadata);

            if (VerifyUserPassword(candidate, userEntry, firstId, revision))
                key = candidate;
        }

        if (key is null)
        {
            failure = "The supplied password did not open the document.";
            return false;
        }

        handler = new StandardSecurityHandler(
            key, revision, streams, strings, embeddedFiles, cryptFilters,
            ownerAccess, permissionBits, encryptMetadata);
        return true;
    }

    /// <summary>Decrypts the payload of a stream.</summary>
    /// <param name="id">The identity of the object the stream belongs to.</param>
    /// <param name="data">The encrypted bytes.</param>
    internal byte[] DecryptStream(PdfObjectId id, ReadOnlySpan<byte> data) => Decrypt(id, data, StreamMethod);

    /// <summary>
    /// Decrypts a stream using its explicit <c>/Crypt</c> decode parameters, or the embedded-file
    /// crypt filter for an <c>/EmbeddedFile</c> stream, before falling back to <c>/StmF</c>.
    /// </summary>
    internal byte[] DecryptStream(PdfObjectId id, ReadOnlySpan<byte> data, PdfDictionary dictionary) =>
        Decrypt(id, data, SelectStreamMethod(dictionary));

    /// <summary>Decrypts a string object.</summary>
    /// <param name="id">The identity of the object the string belongs to.</param>
    /// <param name="data">The encrypted bytes.</param>
    internal byte[] DecryptString(PdfObjectId id, ReadOnlySpan<byte> data) => Decrypt(id, data, StringMethod);

    /// <summary>Encrypts the payload of a stream.</summary>
    /// <param name="id">The identity of the object the stream belongs to.</param>
    /// <param name="data">The plaintext bytes.</param>
    internal byte[] EncryptStream(PdfObjectId id, ReadOnlySpan<byte> data) => Encrypt(id, data, StreamMethod);

    /// <summary>Encrypts a string object.</summary>
    /// <param name="id">The identity of the object the string belongs to.</param>
    /// <param name="data">The plaintext bytes.</param>
    internal byte[] EncryptString(PdfObjectId id, ReadOnlySpan<byte> data) => Encrypt(id, data, StringMethod);

    private CryptMethod SelectStreamMethod(PdfDictionary dictionary)
    {
        PdfValue filters = dictionary.GetFirst(PdfNames.Filter, PdfName.Get("F"));
        int cryptIndex = -1;

        if (filters.AsName == PdfNames.Crypt)
        {
            cryptIndex = 0;
        }
        else if (filters.AsArray is { } array)
        {
            for (int i = 0; i < array.Count; i++)
            {
                if (array.Get(i).AsName == PdfNames.Crypt)
                {
                    cryptIndex = i;
                    break;
                }
            }
        }

        if (cryptIndex >= 0)
        {
            PdfValue parameters = dictionary.GetFirst(
                PdfNames.DecodeParms, PdfName.Get("DP"), PdfName.Get("FDecodeParms"));
            PdfDictionary? cryptParameters = parameters.AsArray is { } parameterArray
                ? cryptIndex < parameterArray.Count ? parameterArray.Get(cryptIndex).AsDictionary : null
                : cryptIndex == 0 ? parameters.AsDictionary : null;
            PdfName filterName = cryptParameters?.GetName(PdfNames.Name) ?? PdfNames.Identity;

            if (filterName == PdfNames.Identity)
                return CryptMethod.None;

            return _cryptFilters.TryGetValue(filterName, out CryptMethod method)
                ? method
                : CryptMethod.None;
        }

        return dictionary.GetName(PdfNames.Type) == PdfNames.EmbeddedFile
            ? EmbeddedFileMethod
            : StreamMethod;
    }

    private byte[] Decrypt(PdfObjectId id, ReadOnlySpan<byte> data, CryptMethod method)
    {
        switch (method)
        {
            case CryptMethod.None:
                return data.ToArray();

            case CryptMethod.Rc4:
                return Rc4.Transform(DeriveObjectKey(id, aes: false), data);

            case CryptMethod.AesV2:
            case CryptMethod.AesV3:
                byte[] key = method == CryptMethod.AesV3 ? _fileKey : DeriveObjectKey(id, aes: true);
                return DecryptAes(key, data);

            case CryptMethod.AesGcm:
                return DecryptGcm(_fileKey, data);

            default:
                return data.ToArray();
        }
    }

    private byte[] Encrypt(PdfObjectId id, ReadOnlySpan<byte> data, CryptMethod method)
    {
        switch (method)
        {
            case CryptMethod.None:
                return data.ToArray();

            case CryptMethod.Rc4:
                return Rc4.Transform(DeriveObjectKey(id, aes: false), data);

            case CryptMethod.AesV2:
            case CryptMethod.AesV3:
                byte[] key = method == CryptMethod.AesV3 ? _fileKey : DeriveObjectKey(id, aes: true);
                return EncryptAes(key, data);

            case CryptMethod.AesGcm:
                return EncryptGcm(_fileKey, data);

            default:
                return data.ToArray();
        }
    }

    /// <summary>
    /// Undoes the AES-GCM wrapping of ISO/TS 32003 clause 5.2: a twelve-byte initialisation vector,
    /// the ciphertext, and a sixteen-byte authentication tag, with no additional authenticated
    /// data. A tag that does not check out means the bytes were changed after they were encrypted,
    /// and the caller is told so rather than handed nothing.
    /// </summary>
    private static byte[] DecryptGcm(byte[] key, ReadOnlySpan<byte> data)
    {
        const int NonceLength = 12;
        const int TagLength = 16;

        if (data.Length < NonceLength + TagLength)
        {
            throw new Diagnostics.PdfTamperedException(
                $"An encrypted object is {data.Length} bytes, too short to hold a vector and a tag.");
        }

        ReadOnlySpan<byte> nonce = data[..NonceLength];
        ReadOnlySpan<byte> tag = data[^TagLength..];
        ReadOnlySpan<byte> cipher = data[NonceLength..^TagLength];

        byte[] plain = new byte[cipher.Length];
        try
        {
            using var aes = new AesGcm(key, TagLength);
            aes.Decrypt(nonce, cipher, tag, plain);
        }
        catch (CryptographicException exception)
        {
            throw new Diagnostics.PdfTamperedException(
                "An encrypted object failed to authenticate, so the file has been altered since it was written.",
                exception);
        }

        return plain;
    }

    private static byte[] EncryptGcm(byte[] key, ReadOnlySpan<byte> data)
    {
        const int NonceLength = 12;
        const int TagLength = 16;

        byte[] output = new byte[NonceLength + data.Length + TagLength];
        Span<byte> nonce = output.AsSpan(0, NonceLength);
        RandomNumberGenerator.Fill(nonce);

        using var aes = new AesGcm(key, TagLength);
        aes.Encrypt(nonce, data, output.AsSpan(NonceLength, data.Length), output.AsSpan(NonceLength + data.Length));
        return output;
    }

    private static byte[] DecryptAes(byte[] key, ReadOnlySpan<byte> data)
    {
        if (data.Length <= 16)
            return [];

        using var aes = Aes.Create();
        aes.Key = key;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;

        ReadOnlySpan<byte> iv = data[..16];
        ReadOnlySpan<byte> body = data[16..];
        int usable = body.Length - (body.Length % 16);
        if (usable == 0)
            return [];

        byte[] plain = aes.DecryptCbc(body[..usable], iv, PaddingMode.None);

        // Strip PKCS#7 padding defensively: damaged files often carry a bogus final byte.
        int pad = plain[^1];
        return pad is >= 1 and <= 16 && pad <= plain.Length ? plain[..^pad] : plain;
    }

    private static byte[] EncryptAes(byte[] key, ReadOnlySpan<byte> data)
    {
        byte[] iv = RandomNumberGenerator.GetBytes(16);
        using var aes = Aes.Create();
        aes.Key = key;

        byte[] cipher = aes.EncryptCbc(data, iv, PaddingMode.PKCS7);
        byte[] output = new byte[16 + cipher.Length];
        iv.CopyTo(output, 0);
        cipher.CopyTo(output, 16);
        return output;
    }

    private byte[] DeriveObjectKey(PdfObjectId id, bool aes)
    {
        Span<byte> input = stackalloc byte[_fileKey.Length + 9];
        _fileKey.CopyTo(input);

        int offset = _fileKey.Length;
        input[offset++] = (byte)id.Number;
        input[offset++] = (byte)(id.Number >> 8);
        input[offset++] = (byte)(id.Number >> 16);
        input[offset++] = (byte)id.Generation;
        input[offset++] = (byte)(id.Generation >> 8);

        if (aes)
        {
            input[offset++] = 0x73;
            input[offset++] = 0x41;
            input[offset++] = 0x6C;
            input[offset++] = 0x54;
        }

        Span<byte> digest = stackalloc byte[16];
        MD5.HashData(input[..offset], digest);

        int length = Math.Min(_fileKey.Length + 5, 16);
        return digest[..length].ToArray();
    }

    private static (
        CryptMethod Streams,
        CryptMethod Strings,
        CryptMethod EmbeddedFiles,
        IReadOnlyDictionary<PdfName, CryptMethod> Filters) ResolveMethods(PdfDictionary encrypt, int version)
    {
        if (version < 4)
            return (CryptMethod.Rc4, CryptMethod.Rc4, CryptMethod.Rc4,
                new Dictionary<PdfName, CryptMethod>());

        PdfDictionary? filters = encrypt.GetDictionary(PdfNames.CF);
        var methods = new Dictionary<PdfName, CryptMethod>();

        if (filters is not null)
        {
            foreach (PdfName name in filters.Keys)
            {
                PdfDictionary? definition = filters.Get(name).AsDictionary;
                PdfName? method = definition?.GetName(PdfNames.CFM);
                methods[name] = method?.Value switch
                {
                    "V2" => CryptMethod.Rc4,
                    "AESV2" => CryptMethod.AesV2,
                    "AESV3" => CryptMethod.AesV3,
                    "AESV4" => CryptMethod.AesGcm,
                    "None" => CryptMethod.None,
                    _ => version >= 5 ? CryptMethod.AesV3 : CryptMethod.Rc4,
                };
            }
        }

        CryptMethod Lookup(PdfName? filterName)
        {
            if (filterName is null || filterName.Value == "Identity")
                return CryptMethod.None;

            // Keep the historical compatibility fallback for a file that names but omits a
            // default filter definition. Explicit /Crypt names use only declared definitions.
            return methods.TryGetValue(filterName, out CryptMethod method)
                ? method
                : version >= 5 ? CryptMethod.AesV3 : CryptMethod.Rc4;
        }

        PdfName streamFilter = encrypt.GetName(PdfNames.StmF) ?? PdfNames.Identity;
        return (Lookup(streamFilter),
                Lookup(encrypt.GetName(PdfNames.StrF) ?? PdfNames.Identity),
                Lookup(encrypt.GetName(PdfNames.EFF) ?? streamFilter),
                methods);
    }

    private static byte[] EncodeLegacyPassword(string password)
    {
        if (password.Length == 0)
            return [];

        return PdfTextEncoding.TryEncodePdfDoc(password, out byte[] encoded)
            ? encoded
            : Encoding.UTF8.GetBytes(password);
    }

    private static byte[] PadPassword(ReadOnlySpan<byte> password)
    {
        byte[] padded = new byte[32];
        int take = Math.Min(password.Length, 32);
        password[..take].CopyTo(padded);
        PaddingBytes[..(32 - take)].CopyTo(padded.AsSpan(take));
        return padded;
    }

    private static byte[] ComputeLegacyKey(
        ReadOnlySpan<byte> password,
        ReadOnlySpan<byte> ownerEntry,
        int permissions,
        ReadOnlySpan<byte> firstId,
        int revision,
        int keyLength,
        bool encryptMetadata)
    {
        using var md5 = MD5.Create();
        byte[] padded = PadPassword(password);
        md5.TransformBlock(padded, 0, padded.Length, null, 0);

        byte[] owner = ownerEntry.Length >= 32 ? ownerEntry[..32].ToArray() : PadPassword(ownerEntry);
        md5.TransformBlock(owner, 0, owner.Length, null, 0);

        byte[] permissionBytes =
        [
            (byte)permissions, (byte)(permissions >> 8), (byte)(permissions >> 16), (byte)(permissions >> 24),
        ];
        md5.TransformBlock(permissionBytes, 0, 4, null, 0);

        byte[] id = firstId.ToArray();
        md5.TransformBlock(id, 0, id.Length, null, 0);

        if (revision >= 4 && !encryptMetadata)
        {
            byte[] marker = [0xFF, 0xFF, 0xFF, 0xFF];
            md5.TransformBlock(marker, 0, 4, null, 0);
        }

        md5.TransformFinalBlock([], 0, 0);
        byte[] digest = md5.Hash!;

        if (revision >= 3)
        {
            for (int i = 0; i < 50; i++)
                digest = MD5.HashData(digest.AsSpan(0, keyLength));
        }

        return digest[..keyLength];
    }

    private static bool VerifyUserPassword(
        byte[] key, ReadOnlySpan<byte> userEntry, ReadOnlySpan<byte> firstId, int revision)
    {
        if (userEntry.Length < 16)
            return false;

        if (revision == 2)
        {
            byte[] expected = Rc4.Transform(key, PaddingBytes);
            return userEntry.Length >= 32 && CryptographicOperations.FixedTimeEquals(expected, userEntry[..32]);
        }

        using var md5 = MD5.Create();
        byte[] padding = PaddingBytes.ToArray();
        md5.TransformBlock(padding, 0, padding.Length, null, 0);
        byte[] id = firstId.ToArray();
        md5.TransformBlock(id, 0, id.Length, null, 0);
        md5.TransformFinalBlock([], 0, 0);

        byte[] value = Rc4.Transform(key, md5.Hash!);
        Span<byte> temporaryKey = stackalloc byte[key.Length];

        for (int i = 1; i <= 19; i++)
        {
            for (int j = 0; j < key.Length; j++)
                temporaryKey[j] = (byte)(key[j] ^ i);

            Rc4.Transform(temporaryKey, value, value);
        }

        return CryptographicOperations.FixedTimeEquals(value, userEntry[..16]);
    }

    private static byte[] RecoverUserPassword(
        ReadOnlySpan<byte> ownerPassword, ReadOnlySpan<byte> ownerEntry, int revision, int keyLength)
    {
        byte[] digest = MD5.HashData(PadPassword(ownerPassword));

        if (revision >= 3)
        {
            for (int i = 0; i < 50; i++)
                digest = MD5.HashData(digest);
        }

        byte[] key = digest[..(revision == 2 ? 5 : keyLength)];
        byte[] value = ownerEntry[..32].ToArray();

        if (revision == 2)
        {
            Rc4.Transform(key, value, value);
            return value;
        }

        Span<byte> temporaryKey = stackalloc byte[key.Length];
        for (int i = 19; i >= 0; i--)
        {
            for (int j = 0; j < key.Length; j++)
                temporaryKey[j] = (byte)(key[j] ^ i);

            Rc4.Transform(temporaryKey, value, value);
        }

        return value;
    }

    private static bool TryOpenRevision5(
        PdfDictionary encrypt,
        string password,
        int revision,
        byte[] ownerEntry,
        byte[] userEntry,
        int permissionBits,
        bool encryptMetadata,
        CryptMethod streams,
        CryptMethod strings,
        CryptMethod embeddedFiles,
        IReadOnlyDictionary<PdfName, CryptMethod> cryptFilters,
        out StandardSecurityHandler? handler,
        out string? failure)
    {
        handler = null;
        failure = null;

        if (userEntry.Length < 48 || ownerEntry.Length < 48)
        {
            failure = "The AES-256 encryption dictionary is incomplete.";
            return false;
        }

        byte[] passwordBytes = SaslPrepare(password);
        byte[] userValidation = userEntry[32..40];
        byte[] userKeySalt = userEntry[40..48];
        byte[] ownerValidation = ownerEntry[32..40];
        byte[] ownerKeySalt = ownerEntry[40..48];
        byte[] userFirst48 = userEntry[..48];

        byte[]? fileKey = null;
        bool ownerAccess = false;

        if (CryptographicOperations.FixedTimeEquals(
                Hash(revision, passwordBytes, ownerValidation, userFirst48), ownerEntry.AsSpan(0, 32)))
        {
            byte[] intermediate = Hash(revision, passwordBytes, ownerKeySalt, userFirst48);
            fileKey = DecryptNoPadding(intermediate, encrypt.Get(PdfNames.OE).AsStringBytes);
            ownerAccess = true;
        }

        if (fileKey is null && CryptographicOperations.FixedTimeEquals(
                Hash(revision, passwordBytes, userValidation, []), userEntry.AsSpan(0, 32)))
        {
            byte[] intermediate = Hash(revision, passwordBytes, userKeySalt, []);
            fileKey = DecryptNoPadding(intermediate, encrypt.Get(PdfNames.UE).AsStringBytes);
        }

        if (fileKey is null || fileKey.Length != 32)
        {
            failure = "The supplied password did not open the document.";
            return false;
        }

        if (!TryValidatePermissions(encrypt, fileKey, permissionBits, encryptMetadata, out failure))
            return false;

        handler = new StandardSecurityHandler(
            fileKey, revision, streams, strings, embeddedFiles, cryptFilters,
            ownerAccess, permissionBits, encryptMetadata);
        return true;
    }

    /// <summary>
    /// Validates the encrypted permission block of revisions 5 and 6. Besides detecting a damaged
    /// encryption dictionary, this is what prevents an attacker from setting PDF MAC permission
    /// bit 13 in the clear <c>P</c> entry and then stripping the authentication code.
    /// </summary>
    private static bool TryValidatePermissions(
        PdfDictionary encrypt,
        byte[] fileKey,
        int permissionBits,
        bool encryptMetadata,
        out string? failure)
    {
        failure = null;
        ReadOnlySpan<byte> encoded = encrypt.Get(PdfNames.Perms).AsStringBytes;

        if (encoded.Length != 16)
        {
            failure = "The AES-256 encryption dictionary carries no valid encrypted permissions block.";
            return false;
        }

        byte[] plain;
        try
        {
            using var aes = Aes.Create();
            aes.Key = fileKey;
            aes.Mode = CipherMode.ECB;
            aes.Padding = PaddingMode.None;
            plain = aes.DecryptEcb(encoded, PaddingMode.None);
        }
        catch (CryptographicException)
        {
            failure = "The encrypted permissions block could not be decrypted.";
            return false;
        }

        int protectedBits = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(plain);
        bool markerMatches = plain[9] == (byte)'a' && plain[10] == (byte)'d' && plain[11] == (byte)'b';
        bool metadataMatches = plain[8] == (encryptMetadata ? (byte)'T' : (byte)'F');

        if (!markerMatches || !metadataMatches || protectedBits != permissionBits)
        {
            failure = "The encrypted permissions block does not match the public encryption settings.";
            return false;
        }

        return true;
    }

    private static byte[] DecryptNoPadding(byte[] key, ReadOnlySpan<byte> data)
    {
        if (data.Length < 32)
            return [];

        using var aes = Aes.Create();
        aes.Key = key;
        return aes.DecryptCbc(data[..32], new byte[16], PaddingMode.None);
    }

    /// <summary>
    /// The password hash of §7.6.4.3.3: plain SHA-256 for revision 5, and the iterated construction
    /// of Algorithm 2.B for revision 6 and for the revision 7 that ISO/TS 32003 adds, which derives
    /// its key exactly as revision 6 does and differs only in the cipher.
    /// </summary>
    internal static byte[] Hash(int revision, ReadOnlySpan<byte> password, ReadOnlySpan<byte> salt, ReadOnlySpan<byte> userData)
    {
        Span<byte> seed = stackalloc byte[password.Length + salt.Length + userData.Length];
        password.CopyTo(seed);
        salt.CopyTo(seed[password.Length..]);
        userData.CopyTo(seed[(password.Length + salt.Length)..]);

        byte[] k = SHA256.HashData(seed);
        if (revision == 5)
            return k;

        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;

        for (int round = 0; ; round++)
        {
            int blockLength = password.Length + k.Length + userData.Length;
            byte[] k1 = new byte[blockLength * 64];
            for (int i = 0; i < 64; i++)
            {
                int offset = i * blockLength;
                password.CopyTo(k1.AsSpan(offset));
                k.CopyTo(k1.AsSpan(offset + password.Length));
                userData.CopyTo(k1.AsSpan(offset + password.Length + k.Length));
            }

            aes.Key = k[..16];
            byte[] e = aes.EncryptCbc(k1, k.AsSpan(16, 16), PaddingMode.None);

            int modulus = 0;
            for (int i = 0; i < 16; i++)
                modulus += e[i];

            k = (modulus % 3) switch
            {
                0 => SHA256.HashData(e),
                1 => SHA384.HashData(e),
                _ => SHA512.HashData(e),
            };

            if (round >= 63 && e[^1] <= round - 31)
                break;
        }

        return k[..32];
    }

    /// <summary>
    /// Prepares a password the way §7.6.4.3.3 asks: SASLprep in principle, UTF-8 truncated to 127
    /// bytes in practice, which is what every producer actually does.
    /// </summary>
    internal static byte[] SaslPrepare(string password)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(password.Normalize(NormalizationForm.FormKC));
        return utf8.Length <= 127 ? utf8 : utf8[..127];
    }

    /// <summary>Builds an encryption dictionary and the handler that matches it.</summary>
    /// <param name="userPassword">The password that opens the document with the given permissions.</param>
    /// <param name="ownerPassword">The password that grants full access.</param>
    /// <param name="permissions">The operations the user password allows.</param>
    /// <param name="encryptMetadata">Whether the metadata stream is encrypted along with everything else.</param>
    /// <param name="dictionary">Receives the <c>Encrypt</c> dictionary to write into the file.</param>
    /// <param name="galoisCounterMode">
    /// Whether to encrypt in Galois/Counter mode, which ISO/TS 32003 adds as <c>AESV4</c> under
    /// version 6 and revision 7. It authenticates the ciphertext as well as concealing it, so a
    /// tampered file fails to decrypt rather than decrypting to something else; the cost is that
    /// readers older than the technical specification cannot open the file at all.
    /// </param>
    /// <param name="requireMac">Whether permission bit 13 shall require an ISO/TS 32004 token.</param>
    internal static StandardSecurityHandler CreateAes256(
        string userPassword,
        string ownerPassword,
        PdfPermissions permissions,
        bool encryptMetadata,
        out PdfDictionary dictionary,
        bool galoisCounterMode = false,
        bool requireMac = false)
    {
        byte[] fileKey = RandomNumberGenerator.GetBytes(32);
        byte[] userBytes = SaslPrepare(userPassword);
        byte[] ownerBytes = SaslPrepare(ownerPassword.Length == 0 ? userPassword : ownerPassword);

        byte[] userValidationSalt = RandomNumberGenerator.GetBytes(8);
        byte[] userKeySalt = RandomNumberGenerator.GetBytes(8);
        byte[] userEntry = new byte[48];
        Hash(6, userBytes, userValidationSalt, []).CopyTo(userEntry, 0);
        userValidationSalt.CopyTo(userEntry, 32);
        userKeySalt.CopyTo(userEntry, 40);

        byte[] userIntermediate = Hash(6, userBytes, userKeySalt, []);
        byte[] userEncryptedKey = EncryptNoPadding(userIntermediate, fileKey);

        byte[] ownerValidationSalt = RandomNumberGenerator.GetBytes(8);
        byte[] ownerKeySalt = RandomNumberGenerator.GetBytes(8);
        byte[] ownerEntry = new byte[48];
        Hash(6, ownerBytes, ownerValidationSalt, userEntry).CopyTo(ownerEntry, 0);
        ownerValidationSalt.CopyTo(ownerEntry, 32);
        ownerKeySalt.CopyTo(ownerEntry, 40);

        byte[] ownerIntermediate = Hash(6, ownerBytes, ownerKeySalt, userEntry);
        byte[] ownerEncryptedKey = EncryptNoPadding(ownerIntermediate, fileKey);

        int permissionBits = unchecked((int)0xFFFFF0C0) | (int)permissions;
        if (requireMac)
            permissionBits &= ~(1 << 12);

        byte[] perms = BuildPerms(fileKey, permissionBits, encryptMetadata);

        // Everything about deriving the key is the same either way; only the cipher differs, and
        // the version and revision that announce it.
        CryptMethod method = galoisCounterMode ? CryptMethod.AesGcm : CryptMethod.AesV3;
        int revision = galoisCounterMode ? 7 : 6;

        var cryptFilter = new PdfDictionary
        {
            [PdfNames.CFM] = PdfValue.Name(galoisCounterMode ? PdfNames.AESV4 : PdfNames.AESV3),
            [PdfName.Get("AuthEvent")] = PdfValue.Name(PdfName.Get("DocOpen")),
            [PdfName.Get("Length")] = PdfValue.Integer(32),
        };

        dictionary = new PdfDictionary
        {
            [PdfNames.Filter] = PdfValue.Name(PdfNames.Standard),
            [PdfNames.V] = PdfValue.Integer(galoisCounterMode ? 6 : 5),
            [PdfNames.R] = PdfValue.Integer(revision),
            [PdfName.Get("Length")] = PdfValue.Integer(256),
            [PdfNames.CF] = PdfValue.Dictionary(new PdfDictionary { [PdfNames.StdCF] = PdfValue.Dictionary(cryptFilter) }),
            [PdfNames.StmF] = PdfValue.Name(PdfNames.StdCF),
            [PdfNames.StrF] = PdfValue.Name(PdfNames.StdCF),
            [PdfNames.U] = PdfValue.String(userEntry),
            [PdfNames.UE] = PdfValue.String(userEncryptedKey),
            [PdfNames.O] = PdfValue.String(ownerEntry),
            [PdfNames.OE] = PdfValue.String(ownerEncryptedKey),
            [PdfNames.P] = PdfValue.Integer(permissionBits),
            [PdfNames.Perms] = PdfValue.String(perms),
        };

        if (!encryptMetadata)
            dictionary.Set(PdfNames.EncryptMetadata, PdfValue.False);

        return new StandardSecurityHandler(
            fileKey, revision, method, method, method,
            new Dictionary<PdfName, CryptMethod> { [PdfNames.StdCF] = method },
            true, permissionBits, encryptMetadata);
    }

    private static byte[] EncryptNoPadding(byte[] key, byte[] data)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        return aes.EncryptCbc(data, new byte[16], PaddingMode.None);
    }

    private static byte[] BuildPerms(byte[] fileKey, int permissions, bool encryptMetadata)
    {
        byte[] block = new byte[16];
        block[0] = (byte)permissions;
        block[1] = (byte)(permissions >> 8);
        block[2] = (byte)(permissions >> 16);
        block[3] = (byte)(permissions >> 24);
        block[4] = 0xFF;
        block[5] = 0xFF;
        block[6] = 0xFF;
        block[7] = 0xFF;
        block[8] = encryptMetadata ? (byte)'T' : (byte)'F';
        block[9] = (byte)'a';
        block[10] = (byte)'d';
        block[11] = (byte)'b';
        RandomNumberGenerator.Fill(block.AsSpan(12, 4));

        using var aes = Aes.Create();
        aes.Key = fileKey;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        return aes.EncryptEcb(block, PaddingMode.None);
    }
}
