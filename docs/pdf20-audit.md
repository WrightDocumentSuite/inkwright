# Alignment with ISO 32000-2

## Before and After

Before this revision the library was aligned with **ISO/DIS 32000-2** — the international standard draft
from 2017-01-09. A draft and a published standard are different documents, and you cannot base a claim of
PDF 2.0 support on a draft.

The source is now **ISO 32000-2:2020, second edition (2020-12), Errata Collection 3 dated 1 June 2026**.
The second edition supersedes and replaces the first (2017), which received a technical revision;
key 2020 amendments are marked in the text with "(2020)".

## Clauses Rewritten in PDF 2.0

The standard itself (clause 0.3) lists what was substantially rewritten. From that list the library
is affected by:

- **7.4.7 "JBIG2Decode filter"** — rewritten in full. Requirements are broken down below; they directly set
  the bounds of the decoder.
- **12.8 "Digital signatures"** — rewritten. Affects `Inkwright.Signing`.
- **14.3 "Metadata"** — rewritten. Affects XMP writing.
- **14.8 "Tagged PDF"** and **14.9 "Repurposing and accessibility support"** — rewritten. Basis
  for the PDF/UA profile.

Not affected: 10.1–10.3 "Rendering" and 11 "Transparency" — the library does not rasterize or blend.

## Updated Normative References

- JBIG2 — now **ISO/IEC 14492:2019** instead of the 2001 edition.
- Open Font Format — now **ISO/IEC 14496-22:2019**.
- ICC.1 — ISO 15076-1:2010 is supplemented by an errata list.

## PDF 2.0 Features Relevant to the Library

New:

- **14.7.4 "Namespaces"** for tagged PDF — basis for ISO/TS 32005 and PDF/UA-2. *Implemented.*
- **14.13 "Associated files"** — together with Application Note 002. *Implemented.*
- **12.8.3.4 "CAdES signatures as used in PDF"**, **12.8.4 "Long term validation"**,
  **12.8.4.3 "Document Security Store"**, **12.8.5 "Document timestamp"** — PAdES levels above B-T.
  *Implemented:* `PdfTimestamper` (DTS), `PdfLongTermValidation` (DSS/VRI), building blocks for
  assembling B-LTA from application-prevalidated validation data.
- **12.8.2.2 "DocMDP"** and **12.8.2.4 "FieldMDP"** — certification signatures and field locking.
  *Implemented:* `CertificationLevel`, `PdfFieldLock`.
- **7.6.5 "Public-key security handlers"** — certificate encryption. *Implemented* on `EnvelopedCms`.
- **7.6.7 "Unencrypted wrapper document"** — not implemented (niche).
- **8.6.5.9 "Use of black point compensation"** — together with Application Note 001. *Implemented.*
- **14.12 "Document parts"** — not implemented (niche).
- UTF-8 support in text strings. *Implemented* on read.
- **Archival conformance for PDF 2.0** — ISO 19005-4 (PDF/A-4), the only PDF/A part written
  against this standard. *Implemented:* levels `A4`, `A4E`, `A4F`, see
  [matrix](pdfa4-matrix.md). Before that the library could write PDF 2.0 and could claim PDF/A, but
  the two did not meet: parts 1–3 require header `%PDF-1.n`.

Extensions of existing features: 256-bit AES, elliptic-curve certificates, Unicode passwords,
page-level output intent (*implemented:* `PdfPage.AddOutputIntent`), `L` key for embedded image data
(*implemented:* declared length takes priority over searching for `EI`, see
[reading.md](reading.md)), structure element references from `GoTo` and `GoToR` actions
(*implemented:* structure destinations, `/SD` bookmark writing).

## 7.4.6 CCITTFaxDecode: Table 11 Parameters

The full normative list the decoder was written against:

- `K` — less than zero: pure two-dimensional encoding (Group 4); zero: pure one-dimensional (Group 3, 1-D);
  greater than zero: mixed (Group 3, 2-D), where after a line encoded one-dimensionally at most
  `K-1` lines encoded two-dimensionally follow. The filter must distinguish the sign of `K` but need not distinguish
  different positive values. Default zero.
- `EndOfLine` — whether end-of-line sequences are present. The decoder must accept them
  always; the flag only states that they are guaranteed. Default false.
- `EncodedByteAlign` — whether to align the start of each line to a byte boundary. Default false.
- `Columns` — width in pixels; if not a multiple of eight, the width of the decoded image
  is padded to the next multiple of eight. Default 1728.
- `Rows` — height in lines; zero or absence means height is not known in advance and data
  ends with an end-of-block sequence. Default zero.
- `EndOfBlock` — whether completion is expected via an end-of-block sequence, which overrides `Rows`.
  The sequence is EOFB or RTC, depending on `K`. Default true.
- `BlackIs1` — whether to treat 1 bits as black, which is the reverse of the usual PDF convention.
  Default false.
- `DamagedRowsBeforeError` — how many damaged rows to tolerate. Applies only when
  `EndOfLine` is true and `K` is non-negative. Default zero.

## 7.4.7 JBIG2Decode: Bounds Set by PDF

The standard narrows JBIG2 to a subset, and that subset defines what the decoder can
encounter at all:

- Encoding per **ISO/IEC 14492:2019**, **without color palette**: type 54 segments are not allowed,
  the color extension flag `COLEXTFLAG` in segment and file headers must be zero.
- **Bitonal data only**, one bit per pixel.
- **Page 1 only** of the stream. Shared segments may still be shared by several XObjects.
- The filter **is not applied to inline images**.
- **Embedded organization** per Annex D.3 of the JBIG2 standard; the two-byte marker from D.3
  must not be present.
- **Sequential organization** per Annex D.1.
- **JBIG2 file header, end-of-page segments, and end-of-file segments are absent.**
- The XObject contains all segments of its page, and the segment page association field in it is always
  equal to one.
- Global segments (page association zero) lie in a separate stream pointed to by
  `/JBIG2Globals`; the stream may be shared by several XObjects.

Recommendation, not requirement: segments should be arranged in ascending segment number order.

## What This Changes in the Code

- The JBIG2 decoder can rely on absence of a file header and on sequential organization —
  parsing starts directly with the first segment header.
- Checking `COLEXTFLAG` and rejecting type 54 segments is a requirement, not optional.
- Edition references in documentation and PDF/A rules were updated to 14492:2019 and 14496-22:2019.
