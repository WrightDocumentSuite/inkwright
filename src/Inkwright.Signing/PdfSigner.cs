using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Inkwright.Content;
using Inkwright.Cos;
using Inkwright.Forms;
using Inkwright.IO;

namespace Inkwright.Signing;

/// <summary>
/// Adds a digital signature to a document. The signature is written as an incremental update, which
/// is not a stylistic choice: the bytes that existed before must survive untouched, or every
/// signature already on the document breaks.
/// </summary>
public static class PdfSigner
{
    /// <summary>
    /// Refuses a digest the machine cannot actually compute. The SHA-3 family reaches .NET through
    /// the platform's cryptographic library, so whether it is there depends on the operating
    /// system; finding out at the moment of signing beats producing a signature nobody can check.
    /// </summary>
    private static void RequireSupportedDigest(System.Security.Cryptography.HashAlgorithmName algorithm)
    {
        if (PdfSignatureAlgorithms.IsSupported(algorithm))
            return;

        string available = string.Join(", ", PdfSignatureAlgorithms.Available.Select(name => name.Name));
        throw new PlatformNotSupportedException(
            $"This machine cannot compute {algorithm.Name} digests. Available here: {available}.");
    }

    /// <summary>
    /// Holds every elliptic-curve signature to the curve/digest table in ISO/TS 32002.
    /// </summary>
    private static void RequireCurveDigestPairing(IExternalSigner signer, PdfSignatureOptions options)
    {
        if (!PdfSignatureAlgorithms.IsEllipticCurve(signer.Certificate))
            return;

        IReadOnlyList<HashAlgorithmName> allowed =
            PdfSignatureAlgorithms.AllowedDigestsForCurve(signer.Certificate);

        if (allowed.Count == 0 || !PdfSignatureAlgorithms.IsAllowedForCurve(signer.Certificate, options.HashAlgorithm))
        {
            string choices = allowed.Count == 0
                ? "no digest: this named curve is not admitted"
                : string.Join(", ", allowed.Select(digest => digest.Name));
            throw new ArgumentException(
                $"ISO/TS 32002 permits {PdfSignatureAlgorithms.CurveName(signer.Certificate)} with {choices}, " +
                $"not {options.HashAlgorithm.Name}.", nameof(options));
        }
    }

    /// <summary>
    /// Names the technical specification behind anything the signature uses that ISO 32000-2 does
    /// not define on its own (ISO 32000-2 §7.12).
    /// </summary>
    private static void DeclareExtensions(PdfDocument document, PdfSignatureOptions options, IExternalSigner signer)
    {
        if (options.FieldLock?.Permissions is not null)
            document.Version = PdfVersion.Max(document.Version, PdfVersion.Pdf20);

        if (options.HashAlgorithm.Name?.StartsWith("SHA3", StringComparison.Ordinal) == true ||
            options.HashAlgorithm.Name == "SHAKE256")
        {
            PdfDeveloperExtensions.DeclareIso(
                document, 32001, ":2022", "https://www.iso.org/standard/45874.html");
        }

        if (PdfSignatureAlgorithms.RequiresIso32002Declaration(signer.Certificate))
        {
            PdfDeveloperExtensions.DeclareIso(
                document, 32002, ":2022", "https://www.iso.org/standard/45875.html");
        }
    }

    /// <summary>Signs a document and writes the signed file.</summary>
    /// <param name="document">The document to sign.</param>
    /// <param name="output">Where to write the signed file.</param>
    /// <param name="options">The certificate, appearance and PAdES level to use.</param>
    public static void Sign(PdfDocument document, Stream output, PdfSignatureOptions options)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(options);

        byte[] signed = Sign(document, options);
        output.Write(signed);
    }

    /// <summary>Signs a document and returns the signed file.</summary>
    /// <param name="document">The document to sign.</param>
    /// <param name="options">The certificate, appearance and PAdES level to use.</param>
    public static byte[] Sign(PdfDocument document, PdfSignatureOptions options)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(options);

        IExternalSigner signer = ResolveSigner(options);
        RequireSupportedDigest(options.HashAlgorithm);
        RequireCurveDigestPairing(signer, options);
        DeclareExtensions(document, options, signer);

        // A signature covers a fixed sequence of bytes, so a document that has never been written
        // is written once first and then signed as an update over that.
        PdfDocument target = document;
        PdfDocument? temporary = null;

        try
        {
            if (!CanAppendTo(document))
            {
                temporary = PdfDocument.Load(document.ToArray());
                target = temporary;
            }

            PdfObjectId signatureId = PrepareField(target, options);

            if (options.AddIntegrityCode)
                PrepareIntegrity(target, signatureId);

            byte[] file = target.ToIncrementalArray(new PdfSaveOptions
            {
                UseObjectStreams = false,
                UpdateModificationDate = false,
                SetProducer = false,
            });

            return Complete(file, signer, options, target);
        }
        finally
        {
            temporary?.Dispose();
        }
    }

    /// <summary>Signs a file on disk and writes the signed copy next to it.</summary>
    /// <param name="sourcePath">The file to sign.</param>
    /// <param name="destinationPath">Where to write the signed file.</param>
    /// <param name="options">The certificate, appearance and PAdES level to use.</param>
    public static void SignFile(string sourcePath, string destinationPath, PdfSignatureOptions options)
    {
        using PdfDocument document = PdfDocument.Load(sourcePath);
        byte[] signed = Sign(document, options);
        File.WriteAllBytes(destinationPath, signed);
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

    private static IExternalSigner ResolveSigner(PdfSignatureOptions options)
    {
        if (options.ExternalSigner is { } external)
            return external;

        if (options.Certificate is not { } certificate)
            throw new ArgumentException("A signature needs either a certificate or an external signer.", nameof(options));

        return new CertificateSigner(certificate, options.Chain);
    }

    private static PdfObjectId PrepareField(PdfDocument document, PdfSignatureOptions options)
    {
        if (options.CertificationLevel != PdfCertificationLevel.None)
            RequireNoEarlierCertification(document);

        PdfForm form = PdfForm.OpenOrCreate(document);
        PdfFormField? existing = form[options.FieldName];
        PdfDictionary field;

        if (existing is not null)
        {
            if (existing.Type != PdfFieldType.Signature)
            {
                throw new InvalidOperationException(
                    $"The form field '{options.FieldName}' already exists and is not a signature field.");
            }

            if (!existing.Inherited(PdfName.Get("V")).IsNull)
                throw new InvalidOperationException($"The signature field '{options.FieldName}' is already filled.");

            field = existing.Dictionary;

            // The existing widgets decide where the signature is visible. Their rectangles win
            // over Appearance, whose coordinates are only meaningful when a new widget is made.
            foreach (PdfDictionary widget in existing.Widgets)
            {
                PdfRectangle bounds = PdfRectangle.FromArray(widget.GetArray(PdfName.Get("Rect")));
                if (!bounds.IsEmpty)
                    widget.Set(PdfName.Get("AP"), PdfValue.Dictionary(BuildAppearance(document, bounds, options)));
            }

            MarkExistingFieldChanged(document, existing);
        }
        else
        {
            PdfPage page = document.Pages.Count > 0
                ? document.Pages[Math.Clamp(options.PageIndex, 0, document.Pages.Count - 1)]
                : document.Pages.Add();

            PdfRectangle bounds = options.Appearance ?? default;
            field = form.CreateWidget(page, bounds, options.FieldName, "Sig");
            field.Set(PdfName.Get("Ff"), PdfValue.Integer(0));

            if (options.Appearance is { } visible)
                field.Set(PdfName.Get("AP"), PdfValue.Dictionary(BuildAppearance(document, visible, options)));
        }

        PdfDictionary signature = BuildSignatureDictionary(options);

        if (BuildReferences(document, options) is { } references)
            signature.Set(PdfName.Get("Reference"), PdfValue.Array(references));

        PdfObjectId signatureId = document.AddObject(PdfValue.Dictionary(signature));
        field.Set(PdfName.Get("V"), PdfValue.Reference(signatureId));

        if (options.CertificationLevel != PdfCertificationLevel.None)
            RegisterCertification(document, signatureId);

        // The lock dictionary must be an indirect reference (§12.7.5.5, Table 235).
        if (options.FieldLock is { } fieldLock)
        {
            PdfDictionary lockDictionary = BuildLockDictionary(fieldLock);
            document.Attach(PdfValue.Dictionary(lockDictionary));
            field.Set(PdfName.Get("Lock"), PdfValue.Reference(document.AddObject(PdfValue.Dictionary(lockDictionary))));
        }

        if (existing is null)
            form.Register(field, options.FieldName);

        // Bit 1 tells a viewer the document holds signatures and must not be saved in a way that
        // rewrites it; bit 2 says the signature is an append-only update.
        form.Root.Set(PdfName.Get("SigFlags"), PdfValue.Integer(3));
        form.MarkChanged();

        return signatureId;
    }

    /// <summary>Marks the field and each indirect widget child for an incremental rewrite.</summary>
    private static void MarkExistingFieldChanged(PdfDocument document, PdfFormField field)
    {
        document.MarkChanged(field.Id);

        if (field.Dictionary.GetArray(PdfName.Get("Kids")) is { } kids)
        {
            for (int i = 0; i < kids.Count; i++)
            {
                if (kids[i].IsReference)
                    document.MarkChanged(kids[i].AsReference);
            }
        }
    }

    /// <summary>
    /// A document can contain only one certification signature (§12.8.2.2): the permissions
    /// dictionary has a single DocMDP slot, and a second certification would silently unseat the
    /// author who signed first.
    /// </summary>
    private static void RequireNoEarlierCertification(PdfDocument document)
    {
        if (document.Catalog.GetDictionary(PdfName.Get("Perms"))?.ContainsKey(PdfName.Get("DocMDP")) == true)
            throw new InvalidOperationException("The document already carries a certification signature.");
    }

    /// <summary>
    /// Builds the signature reference dictionaries (§12.8.1, Table 256) a certification or a field
    /// lock calls for. The FieldMDP parameters repeat what the lock dictionary says because a byte
    /// range signature must keep every value in its dictionary direct, so it cannot just point at
    /// the lock.
    /// </summary>
    private static PdfArray? BuildReferences(PdfDocument document, PdfSignatureOptions options)
    {
        if (options.CertificationLevel == PdfCertificationLevel.None && options.FieldLock is null)
            return null;

        var references = new PdfArray();

        if (options.CertificationLevel != PdfCertificationLevel.None)
        {
            var parameters = new PdfDictionary(3);
            parameters.Set(PdfName.Get("Type"), PdfValue.Name("TransformParams"));
            parameters.Set(PdfName.Get("P"), PdfValue.Integer((int)options.CertificationLevel));
            parameters.Set(PdfName.Get("V"), PdfValue.Name("1.2"));

            references.Add(PdfValue.Dictionary(BuildReference(document, "DocMDP", parameters, options)));
        }

        if (options.FieldLock is { } fieldLock)
        {
            var parameters = new PdfDictionary(4);
            parameters.Set(PdfName.Get("Type"), PdfValue.Name("TransformParams"));
            parameters.Set(PdfName.Get("Action"), PdfValue.Name(fieldLock.Action));

            if (fieldLock.Fields.Count > 0)
                parameters.Set(PdfName.Get("Fields"), PdfValue.Array(FieldNameArray(fieldLock.Fields)));

            parameters.Set(PdfName.Get("V"), PdfValue.Name("1.2"));

            references.Add(PdfValue.Dictionary(BuildReference(document, "FieldMDP", parameters, options)));
        }

        return references;
    }

    private static PdfDictionary BuildReference(
        PdfDocument document, string method, PdfDictionary parameters, PdfSignatureOptions options)
    {
        var reference = new PdfDictionary(5);
        reference.Set(PdfName.Get("Type"), PdfValue.Name("SigRef"));
        reference.Set(PdfName.Get("TransformMethod"), PdfValue.Name(method));
        reference.Set(PdfName.Get("TransformParams"), PdfValue.Dictionary(parameters));

        // FieldMDP names the object the modification analysis starts from; the catalog is the
        // whole document, which is what a form field comparison needs.
        if (method == "FieldMDP")
            reference.Set(PdfName.Get("Data"), PdfValue.Reference(document.CatalogId));

        reference.Set(PdfName.Get("DigestMethod"), PdfValue.Name(DigestMethodName(options)));
        return reference;
    }

    /// <summary>
    /// The digest name Table 256 admits, including the additions made by ISO/TS 32001.
    /// </summary>
    private static string DigestMethodName(PdfSignatureOptions options) => options.HashAlgorithm.Name switch
    {
        "SHA384" => "SHA384",
        "SHA512" => "SHA512",
        "SHA3-256" => "SHA3-256",
        "SHA3-384" => "SHA3-384",
        "SHA3-512" => "SHA3-512",
        "SHAKE256" => "SHAKE256",
        _ => "SHA256",
    };

    private static PdfArray FieldNameArray(IReadOnlyList<string> names)
    {
        var array = new PdfArray();
        foreach (string name in names)
            array.Add(PdfValue.TextString(name));

        return array;
    }

    private static PdfDictionary BuildLockDictionary(PdfFieldLock fieldLock)
    {
        var dictionary = new PdfDictionary(4);
        dictionary.Set(PdfName.Get("Type"), PdfValue.Name("SigFieldLock"));
        dictionary.Set(PdfName.Get("Action"), PdfValue.Name(fieldLock.Action));

        if (fieldLock.Fields.Count > 0)
            dictionary.Set(PdfName.Get("Fields"), PdfValue.Array(FieldNameArray(fieldLock.Fields)));

        if (fieldLock.Permissions is { } permissions and not PdfCertificationLevel.None)
            dictionary.Set(PdfName.Get("P"), PdfValue.Integer((int)permissions));

        return dictionary;
    }

    /// <summary>
    /// Points the permissions dictionary at the certification signature, which is what makes its
    /// restrictions enforceable rather than advisory (§12.8.6, Table 263).
    /// </summary>
    private static void RegisterCertification(PdfDocument document, PdfObjectId signatureId)
    {
        PdfDictionary permissions = document.Catalog.GetDictionary(PdfName.Get("Perms")) ?? new PdfDictionary(1);
        permissions.Set(PdfName.Get("DocMDP"), PdfValue.Reference(signatureId));
        document.Attach(PdfValue.Dictionary(permissions));
        document.Catalog.Set(PdfName.Get("Perms"), PdfValue.Dictionary(permissions));
        document.MarkChanged(document.CatalogId);
    }

    private static PdfDictionary BuildSignatureDictionary(PdfSignatureOptions options)
    {
        var signature = new PdfDictionary(10);
        signature.Set(PdfName.Get("Type"), PdfValue.Name("Sig"));
        signature.Set(PdfName.Get("Filter"), PdfValue.Name("Adobe.PPKLite"));
        signature.Set(PdfName.Get("SubFilter"), PdfValue.Name("ETSI.CAdES.detached"));

        // Both entries are placeholders of an exact width, patched once the file is laid out.
        signature.Set(PdfName.Get("ByteRange"), PdfValue.Array(PdfArray.OfNumbers(0, 9999999999, 9999999999, 9999999999)));
        signature.Set(PdfName.Get("Contents"), PdfValue.String(new byte[options.SignatureSize], hex: true));

        if (options.SignerName is { } name)
            signature.Set(PdfName.Get("Name"), PdfValue.TextString(name));
        else if (options.Certificate is { } certificate)
            signature.Set(PdfName.Get("Name"), PdfValue.TextString(certificate.GetNameInfo(System.Security.Cryptography.X509Certificates.X509NameType.SimpleName, false)));

        if (options.Reason is { } reason)
            signature.Set(PdfName.Get("Reason"), PdfValue.TextString(reason));

        if (options.Location is { } location)
            signature.Set(PdfName.Get("Location"), PdfValue.TextString(location));

        if (options.ContactInfo is { } contact)
            signature.Set(PdfName.Get("ContactInfo"), PdfValue.TextString(contact));

        return signature;
    }

    private static PdfDictionary BuildAppearance(PdfDocument document, PdfRectangle bounds, PdfSignatureOptions options)
    {
        var resources = new PdfDictionary(1);
        document.Attach(PdfValue.Dictionary(resources));

        byte[] content;
        using (ContentCanvas canvas = ContentCanvas.ForStream(document, resources))
        {
            Fonts.PdfFont font = document.Fonts.Helvetica;
            double size = Math.Clamp(bounds.Height / 5, 6, 11);

            canvas.FillColor(PdfColor.Gray(0.96f)).Rectangle(0, 0, bounds.Width, bounds.Height).Fill();
            canvas.StrokeColor(PdfColor.Gray(0.5f)).LineWidth(0.7).Rectangle(0.35, 0.35, bounds.Width - 0.7, bounds.Height - 0.7).Stroke();

            string name = options.SignerName
                ?? options.Certificate?.GetNameInfo(System.Security.Cryptography.X509Certificates.X509NameType.SimpleName, false)
                ?? "Signed";

            double y = bounds.Height - size - 4;
            canvas.DrawText(name, 5, y, font, size, PdfColor.Black);

            y -= size * 1.3;
            canvas.DrawText(
                options.SigningTime.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture),
                5, y, font, size * 0.85, PdfColor.Gray(0.25f));

            if (options.Reason is { } reason)
            {
                y -= size * 1.2;
                canvas.DrawText(reason, 5, y, font, size * 0.85, PdfColor.Gray(0.25f));
            }

            content = canvas.ToArray();
        }

        var stream = PdfStream.FromData(content, compress: false);
        stream.Dictionary.Set(PdfName.Get("Type"), PdfValue.Name("XObject"));
        stream.Dictionary.Set(PdfName.Get("Subtype"), PdfValue.Name("Form"));
        stream.Dictionary.Set(PdfName.Get("BBox"), PdfValue.Array(PdfArray.OfNumbers(0, 0, bounds.Width, bounds.Height)));
        stream.Dictionary.Set(PdfName.Get("Resources"), PdfValue.Dictionary(resources));

        PdfObjectId id = document.AddObject(PdfValue.Stream(stream));

        var appearance = new PdfDictionary(1);
        appearance.Set(PdfName.Get("N"), PdfValue.Reference(id));
        return appearance;
    }

    /// <summary>
    /// Writes the authentication dictionary that points at the signature the code will hang from
    /// (ISO/TS 32004 §6.5.2). It carries no byte range of its own, because the signature's covers
    /// the same bytes.
    /// </summary>
    private static void PrepareIntegrity(PdfDocument document, PdfObjectId signatureId)
    {
        if (!document.WasEncrypted)
        {
            throw new InvalidOperationException(
                "An integrity code is keyed from the file encryption key, so the document must be encrypted.");
        }

        var authentication = new PdfDictionary(2);
        authentication.Set(PdfName.Get("MACLocation"), PdfValue.Name(PdfName.Get("AttachedToSig")));
        authentication.Set(PdfName.Get("SigObjRef"), PdfValue.Reference(signatureId));

        document.Trailer.Set(PdfName.Get("AuthCode"), PdfValue.Dictionary(authentication));

        // The salt the key derivation needs lives in the encryption dictionary, and it belongs to
        // the document from the moment it was encrypted.
        Security.PdfMacSigner.RequireSalt(document);

        PdfDeveloperExtensions.DeclareIso(document, 32004, ":2024", "https://www.iso.org/standard/45877.html");
    }

    private static byte[] Complete(
        byte[] file,
        IExternalSigner signer,
        PdfSignatureOptions options,
        PdfDocument document)
    {
        return SignatureEmbedder.Embed(
            file,
            covered =>
            {
                byte[] cms = CmsBuilder.Build(covered, options, signer);

                // The code goes inside the signature, so it has to be folded in before the space
                // is measured: attaching it afterwards would change the checked length.
                return options.AddIntegrityCode ? Security.PdfMacSigner.Attach(cms, covered, document) : cms;
            },
            $"{nameof(PdfSignatureOptions)}.{nameof(PdfSignatureOptions.SignatureSize)}");
    }
}
