using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Inkwright;
using Inkwright.Cjk;
using Inkwright.Forms;
using Inkwright.Layout;
using Inkwright.Signing;

CjkCMaps.Register();

using PdfDocument document = PdfDocument.Create();
Report.Create()
    .Page(page => page.Content().Text("Native AOT package smoke"))
    .Render(document);

PdfForm.OpenOrCreate(document).Fill(new SmokeForm("ok"));

byte[] unsignedBytes = document.ToArray();
bool sha256Supported = PdfSignatureAlgorithms.IsSupported(HashAlgorithmName.SHA256);

using RSA key = RSA.Create(2048);
var request = new CertificateRequest(
    "CN=Inkwright Native AOT Smoke",
    key,
    HashAlgorithmName.SHA256,
    RSASignaturePadding.Pkcs1);
request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
request.CertificateExtensions.Add(
    new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));

DateTimeOffset now = DateTimeOffset.UtcNow;
using X509Certificate2 certificate = request.CreateSelfSigned(now.AddDays(-1), now.AddDays(1));
byte[] signedBytes = PdfSigner.Sign(document, new PdfSignatureOptions
{
    Certificate = certificate,
    FieldName = "AotSmokeSignature",
    Reason = "Exercise packaged CMS signing under Native AOT",
    SigningTime = now,
});

PdfSignatureInfo signature = PdfSignatureVerifier.Verify(signedBytes).Single();
if (document.Pages.Count != 1 ||
    !sha256Supported ||
    unsignedBytes.Length < 200 ||
    signedBytes.Length <= unsignedBytes.Length ||
    !signature.IsValid ||
    signature.FieldName != "AotSmokeSignature" ||
    signature.SignerCertificate?.Thumbprint != certificate.Thumbprint)
{
    throw new InvalidOperationException("The package smoke test produced an invalid document.");
}

Console.WriteLine(
    $"pages={document.Pages.Count};sha256={sha256Supported};signed={signature.IsValid};bytes={signedBytes.Length}");

[PdfForm]
internal partial record SmokeForm([property: PdfField("value")] string Value);
