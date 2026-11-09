# Signatures

`Inkwright.Signing` does not pull in BouncyCastle: `SignedCms`, `AsnWriter`, and `Rfc3161TimestampRequest` from the base library cover everything PDF signing needs.

## Sign

```csharp
using Inkwright.Signing;

using PdfDocument document = PdfDocument.Load("contract.pdf");

byte[] signed = PdfSigner.Sign(document, new PdfSignatureOptions
{
    Certificate = certificate,          // X509Certificate2 with private key
    Chain = [intermediate, root],
    Reason = "Approved",
    Location = "Moscow",
    FieldName = "Approval",
});

File.WriteAllBytes("contract-signed.pdf", signed);
```

A signature is always written as an incremental update. That is not a stylistic choice: the bytes that were in the file before signing must stay in place, or everything signed earlier breaks.

## How It Works

1. A signature field with a widget and a signature dictionary are added to the document; `/ByteRange` and `/Contents` are placeholders of known length.
2. The document is appended to the end of the file.
3. In the resulting bytes the placeholder is located, the real `/ByteRange` is computed and written back in the same place, padded with spaces to the previous length.
4. CMS SignedData is computed over the entire file except the hexadecimal value of the signature itself.
5. The result is written into the reserved space.

If `FieldName` matches an existing empty `/FT /Sig` field, no new field or widget is created: the signer writes `/V` into the found field and updates the appearance of its existing widgets. Their `/Rect` defines placement; `Appearance` is used as coordinates only when creating a new field. A field of another type or an already filled field is rejected explicitly.

Signed attributes: content type, digest, signing time, and `signingCertificateV2` — the PAdES attribute that binds the signature to a specific certificate. Its ASN.1 is written via `AsnWriter` because `SignedCms` does not model it.

PAdES time is stored only in the signed CMS attribute `signing-time`. Key `/M` in the signature dictionary does not duplicate it: ISO 32000-2 §12.8.3.4.2 allows one time representation but forbids both at once.

## Algorithms

Besides the SHA-2 family, algorithms added to PDF 2.0 by technical specifications are available.

| Algorithm | Source | Availability |
| --- | --- | --- |
| SHA256, SHA384, SHA512 | ISO 32000-2 | always |
| SHA3-256, SHA3-384, SHA3-512 | ISO/TS 32001 | if the system crypto library provides them |
| SHAKE256 | ISO/TS 32001, fixed 64-byte length | if the system crypto library provides it |

SHA-3 in .NET comes from the operating system's crypto library, so availability depends on the system, not the .NET version. Ask ahead of time:

```csharp
if (PdfSignatureAlgorithms.IsSupported(PdfSignatureAlgorithms.Sha3_256))
{
    byte[] signed = PdfSigner.Sign(document, new PdfSignatureOptions
    {
        Certificate = certificate,
        HashAlgorithm = PdfSignatureAlgorithms.Sha3_256,
    });
}
```

`PdfSignatureAlgorithms.Available` lists what actually works on this machine. Attempting to sign with an unavailable algorithm throws `PlatformNotSupportedException` at signing time and lists available ones in the message — better to find out immediately than to ship a signature nobody can verify.

When an algorithm from a technical specification is used, a declaration per ISO 32000-2 §7.12 is written into the catalog extensions dictionary: a reader that encounters an unfamiliar construct should be able to learn which document defines it, not guess.

## Signature Together with Integrity Code

Signature and integrity code answer different questions: the signature says who approved the document; the code says that whoever placed it knew the password. They can be applied together:

```csharp
byte[] signed = PdfSigner.Sign(document, new PdfSignatureOptions
{
    Certificate = certificate,
    AddIntegrityCode = true,
});
```

In a signed revision the code cannot live in the trailer: writing there would change bytes the signature covers. Per ISO/TS 32004 §6.5.2 it hides among the **unsigned** attributes of the signature itself — the only part of the signed container where writing is allowed after signing. The `AuthCode` dictionary in the trailer only points at the signature via `SigObjRef` and carries no byte range of its own: the signature's range plays that role.

The code records two digests — of the document and of the raw signature value — and the verifier must check both. Therefore the MAC detects signature replacement regardless of whether the application trusts its certificate and how ordinary CMS verification ended.

The document must be encrypted and already carry salt, i.e. saved with `AddIntegrityCode`. Otherwise `PdfSigner.Sign` says so directly.

### Edwards Curves

ISO/TS 32002 defines curve–digest pairs in a table rather than leaving them to the signer's choice:

| Curve | Allowed digests |
| --- | --- |
| P-256 | SHA-256, SHA3-256 |
| P-384 | SHA-384, SHA3-384 |
| P-521 | SHA-512, SHA3-512 |
| brainpoolP256r1 | SHA-256/384/512 and matching SHA-3 |
| brainpoolP384r1 | SHA-384/512 and matching SHA-3 |
| brainpoolP512r1 | SHA-512, SHA3-512 |
| Ed25519 / Ed448 | SHA-512 / SHAKE256 respectively |

`PdfSignatureAlgorithms.AllowedDigestsForCurve` returns that table row, and `PdfSigner.Sign` rejects a mismatched digest and unknown or explicitly parameterized EC curves before signing. For Brainpool and Edwards curves, an ISO/TS 32002 declaration is added to the catalog; NIST P-curves are already defined by base ISO 32000-2.

Signing with them is only possible via `IExternalSigner` — .NET has no Ed25519 or Ed448 private-key primitive — and per RFC 8419 the signature covers the full DER-encoded signed attributes, not their digest; the interface provides `SignMessage`, which by default simply hashes and calls `SignHash`, while an Edwards signer overrides it. A certificate with such a key passed directly as `Certificate` throws `PlatformNotSupportedException` pointing to an external signer.

**Edwards curve verification is built in.** Platform `SignedCms` does not support them, so Ed25519 and Ed448 are computed with custom arithmetic on `BigInteger` (`EdwardsCurve`, verified against RFC 8032 §7 vectors). The verifier needs nothing beyond the certificate in the signature; Ed448 requires SHAKE256 from the system and honestly reports when it cannot verify without it.

## Document Timestamp and Long-Term Validation

A separate document timestamp signature (`DocTimeStamp`, ISO 32000-2 §12.8.5) covers the entire file and proves it existed in this form at a moment the service vouches for — without anyone's certificate.

```csharp
byte[] stamped = PdfTimestamper.Timestamp(signed, new PdfTimestampOptions
{
    Authority = new Uri("https://timestamp.example.org/tsa"),
});
```

Together with a validation data store, this is the PAdES level ladder:

```csharp
// B-T: signature with signature timestamp
byte[] signed = PdfSigner.Sign(document, new PdfSignatureOptions
{
    Certificate = certificate,
    Level = PdfSignatureLevel.Timestamped,
    TimestampAuthority = tsa,
});

// B-LT: put certificates and revocation responses in Document Security Store (§12.8.4)
var data = new PdfValidationData();
data.Certificates.Add(certificate);
data.OcspResponses.Add(ocspResponse);
byte[] withLtv = PdfLongTermValidation.Add(signed, "Signature1", data);

// B-LTA: seal all of that with a document timestamp
byte[] archived = PdfTimestamper.Timestamp(withLtv, new PdfTimestampOptions { Authority = tsa });
```

The library does not fetch anything itself: which OCSP/CRL to obtain and whom to trust is application policy, not format policy. It accepts ready blobs and places them in `/DSS` via incremental update strictly after the signature they serve (otherwise the bytes it covers would shift); optionally it also writes a `/VRI` entry keyed by the SHA-1 of the signature's `/Contents`, as Table 261 requires.

These APIs assemble B-LT/B-LTA structure but are not by themselves a conformance claim: the application must verify chain, validity periods, and revocation status of supplied data before adding it.

The default timestamp service is an HTTP request to a `Uri`; behind a proxy or in tests it is replaced by `ITimestampAuthority`. Every RFC 3161 request carries a fresh nonce, and a response whose nonce does not match is rejected.

Use an HTTPS authority endpoint: TLS authenticates the endpoint and protects the exchange in
transit. The library verifies the returned token's imprint, nonce, complete DER framing, and CMS
signature, but it deliberately does not decide whether the TSA certificate chain is trusted; that
chain and revocation policy belongs to the application.

Network signing and timestamping also have asynchronous overloads:

```csharp
using PdfDocument document = await PdfDocument.LoadAsync("contract.pdf", cancellationToken: cancellationToken);

var timestampClient = new HttpTimestampAuthority(tsa, sharedHttpClient)
{
    Timeout = TimeSpan.FromSeconds(20),
    MaxResponseBytes = 512 * 1024,
};

byte[] signed = await PdfSigner.SignAsync(document, new PdfSignatureOptions
{
    Certificate = certificate,
    Level = PdfSignatureLevel.Timestamped,
    TimestampClient = timestampClient,
}, cancellationToken);
```

The supplied `HttpClient` stays owned by the application, so reuse it rather than creating one per
request. `Timeout` bounds one timestamp exchange (in addition to any client policy), and
`MaxResponseBytes` prevents an authority from returning an unbounded body. The asynchronous
document overloads operate on a private incremental snapshot, so the caller-owned `PdfDocument`
remains unchanged after either success or failure and can be reused. Existing implementations that
provide only `ITimestampAuthority.Send` remain source compatible through the interface's default
fallback; that fallback is synchronous, so transports should override `SendAsync` when non-blocking
I/O and cancellation are required. Replaced indirect objects retain their original generation in
the snapshot. Active archival policy is retained as well, including PDF/A-4's intentional omission
of the `/Info` dictionary.

That snapshot requires a trustworthy original cross-reference section. If recovery had to rebuild
the xref table or no valid `startxref` exists, the asynchronous document and file overloads fail
before adding a field. A full rewrite could remove encryption and invalidate earlier byte-range
signatures, so the library does not perform one implicitly. If the damage is understood and those
consequences are acceptable, save an explicitly repaired full copy, inspect it, then reload that
copy before signing.

## Certification Signature and Field Locking

An author signature (`DocMDP`, §12.8.2.2) declares what further changes the document may receive. There is only one in a document: a second would silently dethrone the first author, so the library rejects it.

```csharp
byte[] certified = PdfSigner.Sign(document, new PdfSignatureOptions
{
    Certificate = author,
    CertificationLevel = PdfCertificationLevel.FormFilling,   // forms may be filled, content may not be edited
});
```

A separate signature can freeze a set of form fields (`FieldMDP`, §12.8.2.4): their values become part of what it vouches for.

```csharp
byte[] signed = PdfSigner.Sign(document, new PdfSignatureOptions
{
    Certificate = recipient,
    FieldLock = PdfFieldLock.Include("customer.name", "customer.total"),
});
```

The set is written twice, as the standard requires: in the field's `/Lock` dictionary and is duplicated in the signature's `FieldMDP` transform parameters — because all byte-range signature values must be direct objects and cannot refer to `/Lock` indirectly.

## Signing an Encrypted Document

```csharp
using PdfDocument document = PdfDocument.Load("secret.pdf", new PdfReadOptions { Password = "open" });
byte[] signed = PdfSigner.Sign(document, new PdfSignatureOptions { Certificate = certificate });
```

The `/Contents` value is the only string in the file that must not be encrypted (§7.6.2): it is covered by the byte range, not the document key, and the verifier must read it without knowing the password. Therefore both writing and reading make an exception for it; without that the signature not only fails to verify — it does not parse as ASN.1.

Such a file must be verified via the overload that takes an already opened document: `PdfSignatureVerifier.Verify(document, bytes)`. The bytes-only overload opens the file itself and knows no password.

## Visible Signature

```csharp
var options = new PdfSignatureOptions
{
    Certificate = certificate,
    Appearance = PdfRectangle.FromSize(60, 60, 220, 70),
    PageIndex = 0,
    Reason = "Verified",
};
```

The library draws a frame, the name from the certificate, time, and reason. For a custom look, build an XObject via `ContentCanvas.ForStream` and place it in the widget's `/AP`.

## Signature Timestamp

```csharp
var options = new PdfSignatureOptions
{
    Certificate = certificate,
    Level = PdfSignatureLevel.Timestamped,
    TimestampAuthority = new Uri("https://timestamp.example.org/tsa"),
};
```

The RFC 3161 request is built from `SignerInfo`. The response must consume the complete DER payload,
echo the nonce, match the requested imprint, and carry a valid CMS signature before it is added as
an unsigned attribute. This validates token integrity, not trust in the TSA certificate chain; chain
and revocation policy remain the application's responsibility. This is PAdES B-T level.

## External Keys

A key that cannot be exported is normal for HSMs and cloud signing services:

```csharp
public sealed class HsmSigner : IExternalSigner
{
    public X509Certificate2 Certificate => _certificate;
    public IReadOnlyList<X509Certificate2> Chain => _chain;

    public byte[] SignHash(ReadOnlyMemory<byte> digest, HashAlgorithmName hashAlgorithm) =>
        _hsm.Sign(digest.Span, hashAlgorithm);
}

byte[] signed = PdfSigner.Sign(document, new PdfSignatureOptions { ExternalSigner = new HsmSigner() });
```

For an external key the CMS structure is encoded by the library itself (`DetachedCms`): platform `SignedCms` requires a key it can export, which an HSM will never do. Only the digest leaves the boundary. The same signer can request `PdfSignatureLevel.Timestamped`; after the HSM returns the signature, the library attaches and verifies the RFC 3161 unsigned attribute without asking for the private key again, so external signatures support PAdES B-T as well as B-B.

## Verification

```csharp
foreach (PdfSignatureInfo signature in PdfSignatureVerifier.VerifyFile("contract-signed.pdf"))
{
    Console.WriteLine($"{signature.FieldName}: " +
                      $"intact={signature.IsIntact}, " +
                      $"covers whole file={signature.CoversWholeDocument}, " +
                      $"by={signature.SignerCertificate?.Subject}");
}
```

Verification answers two questions separately: whether the covered bytes changed and whether they cover the entire file. A cryptographically perfect signature over the first half of a file is worth nothing, and merging these answers into one flag would hide that.

`ByteRange` is checked structurally first: an even number of pairs, bounds inside the file, strict order without overlap. `CoversWholeDocument` becomes true only for two segments from byte zero to the exact end of the file, when the only gap matches exactly the string value of `/Contents`; arbitrary four bytes after the range no longer count as an acceptable gap.

When a document is signed twice, the first signature honestly reports that it no longer covers the whole file, and remains intact.

`PdfSignatureInfo` distinguishes document timestamps (`IsDocumentTimestamp`) from certification signatures (`CertificationLevel`) and exposes any `FieldMDP` lock dictionary (`FieldLock`). Embedded signature timestamp is verified for real — `TimestampIntact` says whether the RFC 3161 token and its own signature matched, not merely whether it parsed.

`IsIntact` and `AreSubsequentChangesPermitted` answer different questions, and neither implies the other. `IsIntact` says the signed bytes still match the signature; it stays true when later revisions change the document, because those revisions are outside the signed range. `AreSubsequentChangesPermitted` says whether those later revisions stayed within what the signature allows. Check both, plus `CoversWholeDocument` when the document must be exactly what was signed. `IsValid` requires the signature to cover the whole file, so it is false for any document with later revisions, permitted or not.

When incremental updates follow a signature (`CoversWholeDocument == false`), the verifier compares the signed revision snapshot against the final document and populates `AreSubsequentChangesPermitted` and `ModificationViolations`:

- **`DocMDP` (§12.8.2.2)**:
  - At every level the comparison covers the page's effective resource graph, not only its content stream: a Form XObject, image, font or transparency resource changed behind an unchanged content stream is a violation.
  - `NoChanges` (`P = 1`): any subsequent modification (other than a pure document timestamp or `/DSS` store) is reported as a violation.
  - `FormFilling` (`P = 2`): page tree changes (page count, page content streams, resources, media/crop boxes, rotation) and non-widget annotation additions/removals/modifications are reported as violations, while filling existing form fields and signing are permitted.
  - `FormFillingAndAnnotations` (`P = 3`): page tree and page content/resource modifications are reported as violations, while form filling and annotation updates are permitted.
- **`FieldMDP` (§12.8.2.4)**: if a signature locks all fields (`PdfFieldLockAction.All`), an explicit list (`Include`), or all except a list (`Exclude`), any subsequent change to a locked field's `/V`, `/DV`, or `/Opt` in a later revision sets `AreSubsequentChangesPermitted = false` and names the modified field in `ModificationViolations`.

The library does not verify trust in the chain: which roots to trust is application policy, not format policy.
