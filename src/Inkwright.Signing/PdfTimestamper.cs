using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using Inkwright.Cos;
using Inkwright.Forms;
using Inkwright.IO;

namespace Inkwright.Signing;

/// <summary>What a document timestamp needs beyond the document itself.</summary>
public sealed class PdfTimestampOptions
{
    /// <summary>The timestamp authority to ask over HTTP.</summary>
    public Uri? Authority { get; init; }

    /// <summary>How to reach the authority when plain HTTP does not fit; wins over <see cref="Authority"/>.</summary>
    public ITimestampAuthority? Client { get; init; }

    /// <summary>The digest sent to the authority; SHA-256 unless there is a reason to change it.</summary>
    public HashAlgorithmName HashAlgorithm { get; init; } = HashAlgorithmName.SHA256;

    /// <summary>The name of the signature field the timestamp lives in.</summary>
    /// <remarks>A numeric suffix is appended when a field of this name already exists.</remarks>
    public string FieldName { get; init; } = "Timestamp";

    /// <summary>How many bytes to reserve for the token; raise it for long authority chains.</summary>
    public int SignatureSize { get; init; } = 12288;
}

/// <summary>
/// Adds a document timestamp (ISO 32000-2 §12.8.5): an RFC 3161 token over the whole file, held by
/// a signature field of its own. It proves the document existed in exactly this form at the time
/// the authority vouches for — no signer's certificate involved — and it is the second half of
/// long-term validation: valid DSS data plus a document timestamp lets an application assemble
/// B-LT and, when renewed before the previous token expires, B-LTA.
/// </summary>
public static class PdfTimestamper
{
    /// <summary>Adds a document timestamp and returns the whole updated file.</summary>
    /// <param name="document">The document to timestamp.</param>
    /// <param name="options">The authority to ask and how to lay the field out.</param>
    public static byte[] Timestamp(PdfDocument document, PdfTimestampOptions options)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(options);

        ITimestampAuthority client = CmsBuilder.ResolveAuthority(options.Client, options.Authority);

        if (!PdfSignatureAlgorithms.IsSupported(options.HashAlgorithm))
            throw new PlatformNotSupportedException($"This machine cannot compute {options.HashAlgorithm.Name} digests.");

        PdfDocument target = document;
        PdfDocument? temporary = null;

        try
        {
            if (!CanAppendTo(document))
            {
                temporary = PdfDocument.Load(document.ToArray());
                target = temporary;
            }

            PrepareField(target, options);

            byte[] file = target.ToIncrementalArray(new PdfSaveOptions
            {
                UseObjectStreams = false,
                UpdateModificationDate = false,
                SetProducer = false,
            });

            return SignatureEmbedder.Embed(
                file,
                covered => RequestToken(covered, options, client),
                $"{nameof(PdfTimestampOptions)}.{nameof(PdfTimestampOptions.SignatureSize)}");
        }
        finally
        {
            temporary?.Dispose();
        }
    }

    /// <summary>
    /// Adds a document timestamp asynchronously. Authority I/O is non-blocking when the authority
    /// implements <see cref="ITimestampAuthority.SendAsync"/> asynchronously. The operation stamps
    /// a private snapshot and does not mutate <paramref name="document"/>.
    /// </summary>
    /// <param name="document">The document to timestamp.</param>
    /// <param name="options">The authority to ask and how to lay the field out.</param>
    /// <param name="cancellationToken">Stops the authority request.</param>
    /// <exception cref="InvalidOperationException">
    /// The document was loaded from a source whose cross-reference data had to be rebuilt, so an
    /// identity-preserving incremental snapshot cannot be made safely.
    /// </exception>
    public static async Task<byte[]> TimestampAsync(
        PdfDocument document,
        PdfTimestampOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();

        ITimestampAuthority client = CmsBuilder.ResolveAuthority(options.Client, options.Authority);

        if (!PdfSignatureAlgorithms.IsSupported(options.HashAlgorithm))
            throw new PlatformNotSupportedException($"This machine cannot compute {options.HashAlgorithm.Name} digests.");

        using PdfDocument target = document.CreateIncrementalSnapshot();
        PrepareField(target, options);

        byte[] file = target.ToIncrementalArray(new PdfSaveOptions
        {
            UseObjectStreams = false,
            UpdateModificationDate = false,
            SetProducer = false,
        });

        return await SignatureEmbedder.EmbedAsync(
            file,
            (covered, token) => RequestTokenAsync(covered, options, client, token),
            $"{nameof(PdfTimestampOptions)}.{nameof(PdfTimestampOptions.SignatureSize)}",
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Adds a document timestamp to a complete file, such as one already signed.</summary>
    /// <param name="file">The whole file; its bytes survive untouched at the front of the result.</param>
    /// <param name="options">The authority to ask and how to lay the field out.</param>
    public static byte[] Timestamp(byte[] file, PdfTimestampOptions options)
    {
        ArgumentNullException.ThrowIfNull(file);

        using PdfDocument document = PdfDocument.Load(file);
        return Timestamp(document, options);
    }

    /// <summary>Adds a document timestamp asynchronously to a complete file.</summary>
    /// <param name="file">The whole file; its bytes survive untouched at the front of the result.</param>
    /// <param name="options">The authority to ask and how to lay the field out.</param>
    /// <param name="cancellationToken">Stops the authority request.</param>
    public static async Task<byte[]> TimestampAsync(
        byte[] file,
        PdfTimestampOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(options);

        using PdfDocument document = PdfDocument.Load(file);
        return await TimestampAsync(document, options, cancellationToken).ConfigureAwait(false);
    }

    private static bool CanAppendTo(PdfDocument document)
    {
        try
        {
            return document.ToIncrementalArray(new PdfSaveOptions { UseObjectStreams = false }).Length > 0;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static void PrepareField(PdfDocument document, PdfTimestampOptions options)
    {
        document.Version = PdfVersion.Max(document.Version, PdfVersion.Pdf20);
        PdfForm form = PdfForm.OpenOrCreate(document);
        PdfPage page = document.Pages.Count > 0 ? document.Pages[0] : document.Pages.Add();

        // A timestamp normally has no visual presence (§12.8.5.2), so the widget is empty.
        PdfDictionary field = form.CreateWidget(page, new PdfRectangle(0, 0, 0, 0), UniqueName(form, options.FieldName), "Sig");
        field.Set(PdfName.Get("Ff"), PdfValue.Integer(0));

        // Table 255 wants the timestamp dictionary bare: no signer name, time, reason or location —
        // the token itself is the only authority on when this happened.
        var timestamp = new PdfDictionary(5);
        timestamp.Set(PdfName.Get("Type"), PdfValue.Name("DocTimeStamp"));
        timestamp.Set(PdfName.Get("Filter"), PdfValue.Name("Adobe.PPKLite"));
        timestamp.Set(PdfName.Get("SubFilter"), PdfValue.Name("ETSI.RFC3161"));
        timestamp.Set(PdfName.Get("ByteRange"), PdfValue.Array(PdfArray.OfNumbers(0, 9999999999, 9999999999, 9999999999)));
        timestamp.Set(PdfName.Get("Contents"), PdfValue.String(new byte[options.SignatureSize], hex: true));

        PdfObjectId timestampId = document.AddObject(PdfValue.Dictionary(timestamp));
        field.Set(PdfName.Get("V"), PdfValue.Reference(timestampId));

        form.Register(field, field.GetTextString(PdfName.Get("T"))!);
        form.Root.Set(PdfName.Get("SigFlags"), PdfValue.Integer(3));
        form.MarkChanged();
    }

    private static string UniqueName(PdfForm form, string preferred)
    {
        if (form[preferred] is null)
            return preferred;

        for (int i = 2; ; i++)
        {
            string candidate = $"{preferred}{i}";
            if (form[candidate] is null)
                return candidate;
        }
    }

    /// <summary>
    /// Sends the digest of the covered bytes to the authority and returns the token it issued,
    /// which goes into <c>Contents</c> whole: the token is the signature (§12.8.5.2).
    /// </summary>
    private static byte[] RequestToken(byte[] covered, PdfTimestampOptions options, ITimestampAuthority client)
    {
        byte[] digest = CmsBuilder.Hash(covered, options.HashAlgorithm);

        Rfc3161TimestampRequest request = Rfc3161TimestampRequest.CreateFromHash(
            digest,
            options.HashAlgorithm,
            nonce: CmsBuilder.CreateTimestampNonce(),
            requestSignerCertificates: true);

        byte[] response = client.Send(request.Encode());
        Rfc3161TimestampToken token = CmsBuilder.ValidateTimestampResponse(request, response);

        return token.AsSignedCms().Encode();
    }

    private static async ValueTask<byte[]> RequestTokenAsync(
        byte[] covered,
        PdfTimestampOptions options,
        ITimestampAuthority client,
        CancellationToken cancellationToken)
    {
        byte[] digest = CmsBuilder.Hash(covered, options.HashAlgorithm);

        Rfc3161TimestampRequest request = Rfc3161TimestampRequest.CreateFromHash(
            digest,
            options.HashAlgorithm,
            nonce: CmsBuilder.CreateTimestampNonce(),
            requestSignerCertificates: true);

        byte[] response = await client.SendAsync(request.Encode(), cancellationToken).ConfigureAwait(false);
        Rfc3161TimestampToken token = CmsBuilder.ValidateTimestampResponse(request, response);

        return token.AsSignedCms().Encode();
    }
}
