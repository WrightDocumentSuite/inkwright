using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Inkwright.Cos;

namespace Inkwright.Signing.Tests;

/// <summary>The document and certificate builders the signing tests share.</summary>
internal static class TestDocuments
{
    /// <summary>
    /// A one-page document whose content is left uncompressed, so a test can flip a byte of
    /// visible text and watch a signature notice.
    /// </summary>
    internal static byte[] Build()
    {
        using var document = PdfDocument.Create();
        PdfPage page = document.Pages.Add(PageSize.A4);

        page.SetContent(
            "BT /F1 14 Tf 60 700 Td (Hello, this document is about to be signed.) Tj ET"u8.ToArray(),
            compress: false);

        var font = new PdfDictionary(4);
        font.Set(PdfName.Get("Type"), PdfValue.Name("Font"));
        font.Set(PdfName.Get("Subtype"), PdfValue.Name("Type1"));
        font.Set(PdfName.Get("BaseFont"), PdfValue.Name("Helvetica"));

        var fonts = new PdfDictionary(1);
        fonts.Set(PdfName.Get("F1"), PdfValue.Reference(document.AddObject(PdfValue.Dictionary(font))));
        page.Resources.Set(PdfName.Get("Font"), PdfValue.Dictionary(fonts));

        return document.ToArray(new PdfSaveOptions { UseObjectStreams = false, Compress = false });
    }

    internal static byte[] Sign(byte[] file, PdfSignatureOptions options)
    {
        using PdfDocument document = PdfDocument.Load(file);
        return PdfSigner.Sign(document, options);
    }

    internal static X509Certificate2 CreateCertificate(string subject)
    {
        using RSA key = RSA.Create(2048);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, true));

        DateTimeOffset now = DateTimeOffset.UtcNow;
        return request.CreateSelfSigned(now.AddDays(-1), now.AddYears(1));
    }
}
