# Reading and Editing

## Opening

```csharp
using PdfDocument document = PdfDocument.Load("input.pdf");
using PdfDocument fromBytes = PdfDocument.Load(bytes);
using PdfDocument fromStream = PdfDocument.Load(stream);
using PdfDocument async = await PdfDocument.LoadAsync("input.pdf");
```

`PdfReader` is also available on its own when you need a layer below the model: it returns the trailer, catalog, and any object by number without building the page tree.

```csharp
using Inkwright.IO;

PdfReader reader = PdfReader.Open(bytes);
PdfValue root = reader.Resolve(reader.Trailer[PdfName.Get("Root")]);
```

## Resource Limits for Untrusted Files

Every ordinary read gets a document-local `PdfResourceLimits` ledger by default. It bounds the
buffered input, xref work and sections, indexed objects, object-stream allocations, parsed COS
values and nesting, filter stages, per-stream decoded output, total retained decoded output, and
cumulative bytes materialised across filter stages. Lazy work is charged when an object or stream
is first reached, so a file can open successfully and still be rejected later when code asks for a
pathological page, image, font, or attachment.

```csharp
var limits = new PdfResourceLimits(
    maxInputBytes: 256L * 1024 * 1024,
    maxDecodedBytesPerStream: 32L * 1024 * 1024,
    maxTotalDecodedBytes: 128L * 1024 * 1024,
    maxTotalDecodeWorkBytes: 256L * 1024 * 1024,
    maxObjects: 250_000,
    maxParsedValues: 1_000_000);

try
{
    using PdfDocument document = PdfDocument.Load(
        upload,
        new PdfReadOptions { ResourceLimits = limits });
    string firstPage = document.Pages[0].ExtractText();
}
catch (PdfResourceLimitException exception)
{
    log.Warning("Rejected PDF at {LimitName}: {Observed} > {Limit}",
        exception.LimitName, exception.Observed, exception.Limit);
}
```

`PdfResourceLimits.Default` is deliberately generous for desktop use: 1 GiB input, 128 MiB per
decoded stream, 512 MiB total retained decoded output, 1 GiB of cumulative decode work, 64 filter
stages, two million xref rows, 4096 xref sections, one million indexed objects, two million
object-stream entries, four million parsed values, and 128 nesting levels. Services should choose
lower request-specific values from their upload and memory policy.

Setting `ResourceLimits = null` restores legacy unbounded structural recovery for a trusted batch
or compatibility sweep. It does not remove the process-wide `PdfFilterLimits.MaxDecodedBytes`
ceiling. That static property remains for source compatibility and for standalone filter calls;
prefer per-read limits in concurrent services. A page whose content-stream array crosses its
combined cap raises `PdfResourceLimitException` instead of returning a truncated prefix.

Resource limits bound work without changing the ordinary reader's damage-recovery rules: malformed
filter tails that previously yielded a usable prefix remain recoverable until a budget is actually
exhausted. `PdfResourceLimitException.LimitName` identifies the exact active ceiling, including
`PdfFilterLimits.MaxDecodedBytes` when the process-wide compatibility cap is lower than the
request's per-stream limit. Lazy decoded streams are cached once per document, so concurrent reads
of the same stream share both the bytes and the single budget charge.

The retained-output and work limits are independent. `MaxDecodedBytesPerStream` bounds the final
cached result and the size of any one intermediate buffer; `MaxTotalDecodedBytes` bounds the sum of
final results retained by the document. `MaxTotalDecodeWorkBytes` alone counts the sum materialised
across all filter and predictor stages. A valid multi-stage stream therefore does not spend its
retained-output allowance once per stage.

## Diagnostics Instead of Silent Failures

Everything the reader managed to work around ends up in `LoadDiagnostics` with a code and address:

```csharp
var options = new PdfReadOptions
{
    Password = "secret",
    Recover = true,                            // default
    OnWarning = warning => log.Warning("{Warning}", warning),
};

using PdfDocument document = PdfDocument.Load(path, options);

if (document.Diagnostics.Contains(PdfWarningCode.XrefRebuilt))
    log.Warning("Cross-reference table was rebuilt");
```

Codes are listed in `PdfWarningCode`: from `HeaderNotAtStart` and `StreamLengthMismatch` to
`PageTreeRecovered` and `DecryptionSuspect`. Unrecoverable structure raises `PdfFormatException`;
password failure raises `PdfPasswordException`, authenticated-cipher tampering raises
`PdfTamperedException`, and an exhausted request budget raises `PdfResourceLimitException`.

On the current corpus of 7807 files, 7800 are readable; five rejected files are intentionally damaged fuzzing fixtures without a catalog, header, or readable objects, and two more files are empty and never reach the parser. For all documents admitted by the test policy before rewrite, round-trip completes without failures and preserves the page count.

## Encryption

Reading supports the standard handler for revisions 2–7: RC4-40 and RC4-128, AES-128, AES-256 in both versions, and AES-GCM from PDF 2.0. On the corpus, files of all these variants open successfully. Stream keys are chosen not only by `/StmF`: the explicit `/Crypt` filter respects its own `/DecodeParms /Name` (including `/Identity`), and `/EmbeddedFile` streams without an explicit filter use `/EFF`. This matters for files where only attachments are encrypted and pages are left open.

```csharp
using PdfDocument document = PdfDocument.Load(path, new PdfReadOptions { Password = "owner" });

if (document.WasEncrypted)
    Console.WriteLine($"Granted: {document.SourcePermissions}");
```

A password that does not match throws `PdfPasswordException`. This is deliberate: without a password the document is parsed structurally, but every string and every stream in it is ciphertext, so the file looks like a document with no text. Someone batch-processing files would get empty results and never know. The `WasPasswordSupplied` property distinguishes "password required" from "password incorrect".

For a tool that only sorts files by whether they are locked or not, the exception gets in the way:

```csharp
using PdfDocument document = PdfDocument.Load(path, new PdfReadOptions { OpenLockedDocuments = true });

if (document.WasEncrypted && document.Diagnostics.Contains(PdfWarningCode.UnsupportedSecurityHandler))
    Console.WriteLine("Locked; content cannot be read");
```

Certificate handlers (`/Adobe.PubSec`) open via a certificate with a private key; an example is given below.

Writing encrypts with AES-256:

```csharp
document.Save("secret.pdf", new PdfSaveOptions
{
    Encryption = new PdfEncryptionSettings
    {
        UserPassword = "open-me",
        OwnerPassword = "owner",
        Permissions = PdfPermissions.Print | PdfPermissions.ExtractForAccessibility,
        EncryptMetadata = false,     // XMP stays readable for indexers
    },
});
```

Saving a loaded encrypted document without `Encryption` removes protection; with new settings it re-encrypts with a fresh key. Incremental save reuses the source file's key, so you can append to an encrypted document.

### Galois Counter Mode

By default encryption uses cipher block chaining — version 5, revision 6, method `AESV3`. That is what any reader will open. ISO/TS 32003 adds Galois Counter Mode: version 6, revision 7, method `AESV4`.

```csharp
Encryption = new PdfEncryptionSettings
{
    UserPassword = "open-me",
    UseGaloisCounterMode = true,
}
```

The difference is essentially one thing, and it matters: ordinary encryption only hides content and says nothing about its authenticity. After decrypting a file successfully, the reader learns exactly nothing about whether those are the bytes the author wrote. Galois Counter Mode computes an authentication tag over the ciphertext, and a modified file does not decrypt at all rather than decrypting into something plausible but wrong.

Hence the behavior on read: a tag mismatch raises `PdfTamperedException` rather than returning emptiness. Streams decrypt on access, so the exception arrives either at load — if cross-reference data is corrupted — or on first access to content. Choosing authenticated encryption means asking to be told about this.

The cost is compatibility: a reader older than the technical specification will not open such a file at all. That is why the default stayed the same. When the mode is enabled, an ISO/TS 32003 declaration per ISO 32000-2 §7.12 is written into the catalog extensions dictionary.

### Certificate Encryption

Instead of a password, a document can be encrypted for holders of named X.509 certificates (§7.6.5): the file key is wrapped for each recipient in a CMS envelope, and only someone's private key unwraps it. Everything is built on `EnvelopedCms` from the base library — no BouncyCastle, no native dependencies.

```csharp
// Write: encrypt for two recipients
byte[] file = document.ToArray(new PdfSaveOptions
{
    Encryption = new PdfEncryptionSettings { Recipients = [aliceCertificate, bobCertificate] },
});

// Read: open with your certificate and private key
using PdfDocument opened = PdfDocument.Load(file, new PdfReadOptions
{
    DecryptionCertificate = aliceCertificate,
});
```

256-bit AES is written with crypto filters (`adbe.pkcs7.s5`, `Adobe.PubSec`); the file key is derived from the twenty-byte seed and recipient envelope bytes as §7.6.5.3 requires. On read, the legacy `adbe.pkcs7.s3` and `s4` handlers retain their declared 40–128-bit RC4 key length (`V = 1` is always 40 bit), so old certificate-encrypted archives remain accessible without weakening newly written files. A certificate the document was not encrypted for will not open it; with recovery enabled that is `PdfPasswordException`, not a silent pile of ciphertext.

### Integrity Protection

ISO/TS 32004 describes a separate mechanism — a message authentication code bound to the file encryption key. It answers a question encryption does not: successful decryption says exactly nothing about whether those are the bytes the author wrote. A correct code says that whoever placed it knew the password.

To write:

```csharp
document.Save("sealed.pdf", new PdfSaveOptions
{
    AddIntegrityCode = true,
    Encryption = new PdfEncryptionSettings { UserPassword = "open-me" },
});
```

To verify:

```csharp
PdfMacIntegrity integrity = PdfMacIntegrity.Read(document);

if (integrity.IsVerified)
    Console.WriteLine("File is intact and authentic");
else if (integrity.IsPresent)
    Console.WriteLine(integrity.VerificationNote);
else if (integrity.IsRequired)
    Console.WriteLine("Required AuthCode was removed");
```

There are two checks, and they answer different questions. `IsAuthentic` confirms the HMAC was derived from this document's encryption key, proving the token attributes are genuine. `MatchesContent` requires the full binding chain: the authenticated attributes' `messageDigest` matches the payload, `dataDigest` matches the covered bytes, and the signature token's `signatureDigest` matches the raw signature value. The token can be genuine and yet stale: that happens when the document was incrementally updated and the code was not recalculated.

The full chain: HKDF-SHA256 from the file key with salt `KDFSalt` and label `PDFMAC` yields the wrapping key; RFC 3394 unwraps the code key with it; HMAC-SHA256 over CMS authenticated attributes is compared to the stored value, after which the required `messageDigest` links those attributes to the payload. Verified on 94 files from the iText test set, including SHA-2 and system-available SHA-3 variants.

The salt is one per document lifetime per the standard, so only `AddIntegrityCode` writes it during encryption. You can sign a document with a code (see [signing.md](signing.md)), but the salt must be set in advance: the encryption dictionary is the only object written in plaintext, and it cannot be rewritten in an incremental update.

The thirteenth permissions bit is cleared in the process. For the password handler the same value is written to open `P` and encrypted `Perms`; for the certificate handler — inside each recipient's CMS envelope. The reader compares protected and open values and reports `IsRequired` even if an attacker completely removed `AuthCode`.

The library reports permissions but does not enforce them: a ban on printing or copying is guidance for the reader, not a lock. Code that must comply should check `SourcePermissions` itself.

## Pages

```csharp
PdfPage page = document.Pages[0];

page.Rotation = 90;
page.CropBox = page.MediaBox.Deflate(20);

document.Pages.Move(0, 3);
document.Pages.RemoveAt(1);
document.Pages.Insert(0, PageSize.A4.Landscape);
```

Import from another document pulls everything the page reaches and copies shared objects once:

```csharp
using PdfDocument source = PdfDocument.Load("appendix.pdf");
document.Pages.ImportAll(source);
```

Bookmarks, destinations, fields, and structure move too — details in [merging.md](merging.md).

### Painted rules

`page.ExtractLines()` reports straight vector rules in visible top-left page coordinates. It walks
nested Form XObjects and understands both ordinary stroked paths and the thin dark filled
rectangles many Office/PDF producers use for table borders. Pale fills and large filled rectangles
are not promoted to lines, so cell shading does not become a black frame during fixed-layout
conversion.

```csharp
foreach (PdfLineSegment line in page.ExtractLines())
    Console.WriteLine($"{line.Start} → {line.End}, {line.Width:0.##} pt");
```

### Bordered tables

`page.ExtractTables()` connects those painted rules into grids and assigns visible glyphs to the
cells they enclose. An absent internal rule becomes a row or column span, so a multi-level header
does not have to be flattened merely because the PDF has no structure tree. Returned coordinates
use the same rotation-normalised top-left page space as `ExtractLines()`.

```csharp
foreach (PdfTable table in page.ExtractTables())
{
    foreach (PdfTableCell cell in table.Cells)
        Console.WriteLine($"r{cell.Row} c{cell.Column} {cell.RowSpan}x{cell.ColumnSpan}: {cell.Text}");
}
```

The extractor is deliberately conservative: a lone decorative rectangle is ignored, curved or
borderless tables are not guessed, and safety limits in `TableExtractionOptions` cap pathological
grids. Applications can combine this geometry result with Tagged PDF structure when both exist.

### Physical Redaction

Drawing a black rectangle over sensitive words leaves the underlying glyphs and images intact in the content stream for anyone who selects text or extracts streams. `page.Redact`, `page.RedactText`, `page.ApplyRedactions`, `document.RedactText`, and `document.ApplyRedactions` physically scrub the content streams:

```csharp
using Inkwright.Redaction;

PdfRedactionResult result = document.RedactText(
    "Account 4401-9921",
    new PdfRedactionOptions
    {
        FillColor = PdfColor.Black,
        OverlayText = "[REDACTED]",
        OverlayTextColor = PdfColor.White,
    });

Console.WriteLine($"Removed {result.GlyphsRemoved} glyphs and {result.ImagesRemoved} images");
document.Save("sanitized.pdf", new PdfSaveOptions { RemoveUnusedObjects = true });
```

Character codes whose bounding boxes intersect a redaction rectangle are stripped from `Tj`, `TJ`, `'`, and `"` operators and replaced by exact `TJ` positioning adjustments so surviving text on the same line does not shift. Intersecting inline images, image XObjects (plus unreferenced `/XObject` resource entries), and annotations are removed before painting the redaction box as an `/Artifact`. Always save with ` Save(...)` (full rewrite) and `RemoveUnusedObjects = true` rather than `SaveIncremental`, because an incremental update appends to the file without erasing earlier revisions.

Text alternatives go with the content they describe. `ActualText`, `Alt` and `E` are removed from each marked-content sequence containing a removed glyph or image, and from the structure element that owns it plus that element's ancestors, because they restate the removed content and nothing maps their characters to individual glyphs. After a partial overlap, extraction therefore falls back to the surviving glyphs. `PdfRedactionResult.SemanticEntriesRemoved` counts the entries removed.

Redaction is local to the placement it selects. A Form XObject drawn on several pages, or twice on one page, is copied for the placement that changes; the others keep the original. To remove something everywhere it appears, use `document.RedactText`, which visits every page; nothing changes a shared object for all its placements at once. A form nothing else draws keeps its object number, so structure references to it stay valid. Shared resource dictionaries lose an entry only once no content stream still uses it.

When content cannot be checked against the regions, the operation throws `PdfRedactionException` and leaves that page's content as it was: text in a font that cannot be resolved, Form XObjects nested more than 12 levels deep or drawing themselves. Treat this as a failed redaction and do not publish the file. Text inside tiling patterns and Type 3 glyph procedures is not examined, and outlines, metadata and attachments are outside the operation.

## How Pages Are Named

Page number and page label are different things. A document whose front matter runs i, ii, iii and the main body starts again at one says so here; a viewer showing "page 3 of 40" shows exactly that, not the index.

```csharp
IReadOnlyList<string> labels = document.PageLabels.All();   // ["i", "ii", "iii", "1", "2", ...]
string third = document.PageLabels[2];

document.PageLabels.Set(
[
    new PageLabelRange(0, PageLabelStyle.LowercaseRoman),
    new PageLabelRange(3, PageLabelStyle.Decimal, prefix: "A-", start: 1),
]);
```

The tree must start with a range for page zero (§12.4.2); if the caller did not provide one, it is added automatically. Letters run A…Z, then AA…ZZ, then AAA…ZZZ.

## Layers

```csharp
foreach (PdfLayer layer in document.OptionalContent.Layers)
    Console.WriteLine($"{layer.Name}: {(layer.IsVisible ? "visible" : "hidden")}");
```

Layers hold what a page draws but does not always show: drawing variants in another language, a watermark for print only. Text extraction may skip them — `TextExtractionOptions.SkipHiddenLayers`.

Not only ON and OFF states are considered, but also the membership dictionary policy `/P` (AllOn, AnyOn, AnyOff, AllOff) and visibility expressions `/VE` with nested `/And`, `/Or`, and `/Not`.

## Filters

All filters from Table 6 are read, plus `BrotliDecode` on top. It appears in no part of ISO 32000: it is a Mozilla extension, and a file with it is not conforming PDF. Reading it costs almost nothing, so such a file opens instead of showing blank pages; the library cannot write it and does not. The PDF/A validator is unaffected: it checks the Table 6 whitelist separately.

## Inline Images: Where the Data Ends

The `BI … ID … EI` construct (§8.9.7) has no length in stream syntax, and historical practice is to search for `EI` standing as its own token. That is a guess: compressed data may contain those two bytes, and then the image is truncated mid-stream while the tail is parsed as operators.

PDF 2.0 closed the hole with key `L` (short for `Length`), which "shall be present on all inline images". The reader trusts it first: if the declared length fits in the stream and leads exactly to `EI`, data is taken by it. If it does not match, the file is already broken and the old heuristic applies instead of truncating by a number that obviously lies. An unencoded image without `L` is still measured by its own geometry.

A separate subtlety from the same section: usually exactly one whitespace character follows `ID`, but if the last filter in the chain is `ASCIIHexDecode` or `ASCII85Decode`, there may be any amount of whitespace, and it is not part of the data.

Abbreviated inline-image names are expanded by context. `/I` is the `Interpolate` key in the image
dictionary, but the same `/I` means `Indexed` when it appears as a colour-space value (including
inside an array). Treating both positions alike changes palette images into a boolean option and
silently produces the wrong pixels.

## Two Ways to Save

**Full rewrite** rebuilds the file from scratch. Objects the model never parsed are copied byte for byte; object numbers are preserved.

```csharp
document.Save("out.pdf");
document.Save("out.pdf", new PdfSaveOptions { RemoveUnusedObjects = true });   // discard junk
document.Save("out.pdf", new PdfSaveOptions { UseObjectStreams = false });     // classic xref
```

**Incremental save** appends changes to the end without touching the original bytes. It is the only way to change a signed document without breaking signatures already in it.

```csharp
document.SaveIncremental("out.pdf");
```

The model tracks what changed: objects created via `AddObject` and those marked `MarkChanged`. Page and info dictionary properties mark themselves; after editing someone else's dictionary directly, call `document.MarkChanged(id)`.

Incremental save refuses to work if the source file's cross-reference table had to be rebuilt: an appended section would reference bytes that could never be trusted.
