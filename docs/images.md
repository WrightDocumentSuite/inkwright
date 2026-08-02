# Images

## Embedding

JPEG is placed in the file byte for byte; PNG is embedded without re-encoding—the deflate stream that PNG already carries is the same deflate stream PDF wants. Only images with an alpha channel are parsed, and only enough to split it into a soft mask.

```csharp
PdfImage image = PdfImage.Load(document, @"photo.jpg");
canvas.DrawImage(image, 72, 400, width: 200, height: 150);
```

## Extraction

The reverse path goes from XObject to pixels. The result is RGBA with one byte per channel, first row at the top—what any image consumer expects and what all decoders and color spaces converge on.

```csharp
foreach (PdfPageImage found in page.Images)
{
    Console.WriteLine($"{found.Name}: {found.Width}×{found.Height} {found.Codec}");

    if (!found.CanDecode)
        continue;

    PdfImageData pixels = found.Decode();
    pixels.SavePng($"{found.Name}.png");
}
```

`page.Images` finds images that the page draws inside forms as well, and finds each one only once no matter how many times the content stream draws it.

## What happens during decoding

1. **Unpacking**: filters are removed by the usual pipeline; it stops at the image codec, and the appropriate decoder takes over.
2. **Sample expansion**: 1, 2, 4, 8, or 16 bits per component are expanded into numbers. Each row is padded to a byte boundary, so a one-bit-wide image three pixels wide uses one byte per row, not three consecutive bits.
3. **Decode array**: each component is mapped to its declared range. Defaults come from Table 90—for indexed space that is the full range expressible by the sample depth; for Lab, lightness goes to one hundred, not one.
4. **Color**: values pass through the color space and become RGB.
5. **Transparency**: one of the three masks from §8.9.6 is applied.

## Three kinds of masks

- **Stencil** (`/ImageMask true`)—not color but shape: it paints where the sample is zero and lets the page show through everywhere else.
- **Soft mask** (`/SMask`)—a grayscale image whose values are alpha. If its size does not match the image size, it is stretched.
- **Explicit mask** (`/Mask` with a stream)—a stencil marking what to hide; areas it covers become transparent.
- **Color key** (`/Mask` with an array)—ranges; falling inside them removes the pixel. The check uses samples as stored, not the colors they represent after conversion—as the specification requires.

The soft mask `/Matte` key (§11.6.5) is removed before color-space conversion: the original color is recovered from pre-multiplied components and alpha. That avoids a color halo on semi-transparent edges from how the image was prepared.

## Color spaces

All are converted to RGB because that is what the pixel consumer needs.

| Space | How it is handled |
| --- | --- |
| DeviceGray, DeviceRGB, DeviceCMYK | directly; CMYK via subtraction (§8.6.4.4) |
| CalGray, CalRGB | gamma and matrix map components to CIE XYZ; white point is adapted to D65, then sRGB is obtained |
| Lab | via the declared white point and the sRGB transfer function |
| ICCBased | via `/Alternate`; without it, via `/N` |
| Indexed | lookup in the palette; the sample is an index, not a value |
| Separation, DeviceN | via tint transform; `/None` paints nothing |
| Pattern | the base space is used |

## Functions

Functions from §7.10 live separately and are needed for more than images: tint transforms, shadings, and transfer functions ask for them.

```csharp
PdfFunction? function = PdfFunction.Read(value);
double[] color = function!.Evaluate(0.5);
```

All four types are read: sampled (0), exponential (2), stitched (3), and PostScript calculator (4). The calculator’s trigonometry is in degrees, as Table 42 requires, and `atan` returns an angle from zero to three hundred sixty.

Cubic interpolation (`/Order 3`) for the sampled type is computed by the tensor product of Catmull–Rom splines across all input axes. When `Size < 4` on at least one axis, a request for cubic interpolation is ignored per §7.10.2.

## Codecs

| Codec | Status |
| --- | --- |
| FlateDecode, LZWDecode, RunLengthDecode, ASCII85, ASCIIHex | decoded |
| DCTDecode (JPEG) | sequential baseline is decoded, including subsampling, restart markers, CMYK, Adobe marker inversion, and PDF `ColorTransform` |
| DCTDecode progressive | rejected with a clear message |
| CCITTFaxDecode | decoded: one-dimensional MH, two-dimensional MR and MMR |
| JBIG2Decode | decoded |
| JPXDecode (JPEG 2000) | not decoded |

Rejection is always explicit: `PdfImageReader.CanDecode` answers in advance, and `Decode` throws `NotSupportedException` with the codec name. Raw data remains available through the stream itself, so JPEG 2000 can be handed to an external decoder without re-parsing the file.

Progressive JPEG is rejected on purpose: decoding it incorrectly is worse than not decoding at all.

For a JPEG without an Adobe APP14 marker, `/DecodeParms /ColorTransform` decides whether three- or
four-component samples are colour transformed. If both are absent, the PDF default is YCbCr for
three components and raw samples otherwise. An APP14 marker takes precedence over the PDF entry,
as ISO 32000 requires; this matters for files whose bytes are valid JPEG but whose samples are
already RGB or CMYK.

### Fax encodings

`CCITTFaxDecode` implements ITU-T T.4 and T.6 encodings in full: one-dimensional (`K` equals zero), two-dimensional mixed (`K` greater than zero), and pure two-dimensional Group 4 (`K` less than zero), with all parameters from Table 11—`EncodedByteAlign`, `BlackIs1`, `EndOfLine`, `DamagedRowsBeforeError`, `EndOfBlock`.

Code tables are transcribed from printed Tables 2, 3a, 3b, 4, and 5 of Recommendation T.4 and verified against the reference implementation published with ITU-T T.88. They live in [CcittCodes.cs](../src/Inkwright/Images/Ccitt/CcittCodes.cs) in the same form as printed in the standard: one binary string per run length. A table copied from the specification is worth something only if you can read it back and check it against the page it came from.

A row is stored not as pixels but as color-change positions—that is what makes two-dimensional modes cheap: a row usually almost matches the previous one and is encoded as the difference between their change points.

### JBIG2

All encoding methods used by scanners are implemented.

| What | T.88 section | Status |
| --- | --- | --- |
| MQ arithmetic decoder | Annex E | verified bit-by-bit with Annex H.2 test vector—all 256 decisions |
| Integer decoding, symbol IDs | Annex A | |
| Generic region: templates 0–3, adaptive pixels, `TPGDON`, MMR | §6.2 | |
| Refinement: templates 0 and 1, `TPGRON` | §6.3 | |
| Text region: stripes, reference corners, transposition, instance refinement | §6.4 | |
| Symbol dictionary: height classes, import and export, aggregate refinement | §6.5 | |
| Halftone region and pattern dictionary, gray planes in Gray code | §6.6, §6.7, Annex C | |
| Huffman tables B.1–B.15 and custom tables (segment 53) | Annex B | |
| Intermediate regions and auxiliary buffers | §8.2 | |
| Page Information without a region, including the default pixel | §7.4.8 | decoded |
| Extension segments and comments | §7.4.14 | known comments accepted; unknown optional extensions skipped; unknown necessary extensions rejected |

Symbol dictionaries may retain their generic and refinement MQ statistics for a later dictionary. The decoder snapshots both context arrays, restores an independent copy from the last referred symbol dictionary, and requires the Huffman/refinement/template/AT configuration mandated by T.88 §7.4.2.2 to match. Independent copies matter: two dictionaries may both reuse one snapshot and must not teach each other different probabilities.

Refinement aggregates use the text-region procedure from T.88 §6.4. A strip's terminating
`DeltaS` out-of-band value is consumed even when that strip placed the last advertised instance;
otherwise the following symbol-width or Huffman field would begin at the wrong bit. Arithmetic and
Huffman refinement-aggregate variants are covered by byte-for-byte image comparisons.

The necessary bit in an extension type is treated as a correctness boundary. A known comment or an
unknown optional extension cannot change the rendered page and may be ignored; an unknown necessary
extension could change it and therefore fails explicitly. Truncated extension headers also fail
instead of being mistaken for optional data.

Not decoded: extended templates with twelve adaptive pixels.

The key check is the `bitmap-*.pdf` family in the corpus: 96 files encode the same 399×400 image in every possible way (each reference corner, transposition, each composition operator, negative offset, refinement, Huffman, retained contexts, halftone, wide segment numbers), and every form now yields byte-identical results. Page-information-only streams also produce the blank page defined by their default pixel, using the PDF Image XObject dimensions for the returned sample matrix. With known corpus credentials, all 142 of 142 JBIG2 images decode versus 48 before the original implementation; the generic locked-document harness reaches 141 of 142 because it does not supply the password for one otherwise valid fixture.

Restrictions PDF imposes on JBIG2 (ISO 32000-2 §7.4.7) are enforced: color palette (segment type 54) and the color extension flag are rejected because the standard forbids them. A JBIG2 file header inside a PDF stream is forbidden but appears and is skipped silently. Details are in [pdf20-audit.md](pdf20-audit.md).
