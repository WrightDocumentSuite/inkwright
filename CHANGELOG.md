# Changelog

Notable user-visible changes are recorded here. The project follows semantic versioning while its
public API is pre-1.0; breaking changes are called out explicitly.

## Unreleased

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
