# Changelog

Notable user-visible changes are recorded here. The project follows semantic versioning while its
public API is pre-1.0; breaking changes are called out explicitly.

## Unreleased

## 0.1.5 — 2026-11-09

### Added

- CI validates one sample per PDF/A and PDF/UA claim with veraPDF, independently of the library's
  own validators.

### Changed

- Redaction now refuses with `PdfRedactionException` when content cannot be checked against the
  regions (unresolved font, Form XObjects nested past 12 levels, or a form that draws itself)
  instead of returning normally with that content left in place.

### Fixed

- Text extraction from Standard 14 Symbol fonts without explicit encoding now uses the built-in
  Greek and mathematical character mapping instead of Latin StandardEncoding.
- Embedded TrueType CID fonts now declare empty glyph slots retained by subsetting in `CIDSet`,
  fixing independent PDF/A-2/3 and PDF/UA-1 validation failures.
- PDF/UA-2 declarations select PDF 2.0 and supply a missing namespace on a single `Document` root;
  the validator now rejects the wrong version and root namespace.

- Redaction removes `ActualText`, `Alt` and `E` from marked-content sequences and structure
  elements whose content it touched; they used to keep the removed text extractable.
  `PdfRedactionResult.SemanticEntriesRemoved` reports how many went.
- Redacting a Form XObject affects only the selected placement. Other pages and other placements
  of the same form keep the original, and image entries are kept while another page draws them.
- DocMDP and FieldMDP verification compares each page's resource graph, so changing a Form
  XObject, image or font behind an unchanged content stream is reported as a modification.
- An image whose `/SMask` or `/Mask` refers back to itself raises `PdfFormatException` instead of
  overflowing the stack and ending the process; over-long mask chains raise
  `PdfResourceLimitException`.

## 0.1.4 — 2026-11-08

### Added

- Per-document `PdfResourceLimits` with typed `PdfResourceLimitException` failures for untrusted
  input, xref, object, parser, and stream-decoding budgets.
- Cancellation-aware asynchronous RFC 3161 signing and document-timestamp APIs, including a
  reusable `HttpTimestampAuthority` transport.
- Cross-platform CI, locked dependency graphs, package/API/Source Link validation, Native AOT
  package smoke tests, corpus automation, and fuzz regression replay.
- Source-generator diagnostics for unsupported form shapes and types that cannot be reconstructed.
- Physical PDF redaction (`Inkwright.Redaction`: `PdfPage.Redact`, `PdfPage.RedactText`,
  `PdfPage.ApplyRedactions`, `PdfDocument.RedactText`, `PdfDocument.ApplyRedactions`, and
  `PdfAnnotationCollection.AddRedaction`) that rewrites page and Form XObject content streams to
  remove intersecting glyphs (with `TJ` kerning compensation), inline/XObject images, and
  annotations before painting the redaction overlay artifact.
- `DocMDP` (`P = 1, 2, 3`) and `FieldMDP` (`All`, `Include`, `Exclude`) modification verification
  across incremental PDF revisions in `Inkwright.Signing` (`PdfSignatureInfo.FieldLock`,
  `AreSubsequentChangesPermitted`, `ModificationViolations`).

### Changed

- Pin SDK 10.0.403 exactly and refresh SDK-owned linker dependencies in the committed lock files;
  servicing SDK selection must not silently change a locked restore.

- The package version is `0.1.4` and package validation compares against the latest
  published `0.1.3` API baseline.
- Test, interoperability, cryptography, and benchmark dependencies are pinned centrally to their
  audited versions and restored from committed lock files.

### Fixed

- Form mappings now handle C# keyword identifiers, nested generics, readonly/required members,
  nullable and escaped attribute literals, and unsupported shapes without crashing compilation.
- Asynchronous signing and timestamping are transactional for caller-owned documents: cancellation
  or TSA failure no longer leaves an abandoned signature field behind; non-zero object generations,
  encryption, existing signatures, and PDF/A-4 information-dictionary suppression survive the
  private snapshot. Sources whose cross-reference table had to be rebuilt are rejected rather than
  silently rewritten.
- RFC 3161 responses must echo the request nonce, consume the complete DER payload, and carry a
  cryptographically valid CMS signature before a token is embedded.
- Reader resource accounting bounds cumulative decode work and structural parsing without caching
  a truncated stream as a successful decode.
- `SHAKE256` detached CMS signing and verification in `Inkwright.Signing` now route through the
  custom ASN.1 CMS builder and verifier on platforms where `Shake256.IsSupported` is true but
  `SignedCms` does not recognize the XOF OID (`2.16.840.1.101.3.4.2.18`).

Previous immutable package artifacts are available from NuGet. Source tags are available from
`v0.1.2` onward; every new release must add its matching tag as described in the release checklist.
