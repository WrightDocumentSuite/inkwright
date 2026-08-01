# Benchmarks

```bash
dotnet run -c Release --project benchmarks/Inkwright.Benchmarks -- --filter *
```

Machine: Intel Core i7-8700, Windows Server 2022, .NET 10.0.10, BenchmarkDotNet 0.15.8.

Comparison is against [PDFsharp 6.2](https://www.nuget.org/packages/PDFsharp) and
[PdfPig 0.1.11](https://www.nuget.org/packages/PdfPig) — the only PDF libraries for .NET that can be
benchmarked side by side without native dependencies or AGPL-licensed code. QuestPDF is not included: it
pulls in ~50 MB of native Skia per RID, and measuring managed code against it is unfair both ways.

## Report generation

Table with 40 rows per page, repeating header, footer with page numbers. PDFsharp receives pre-placed
drawing calls — nothing to lay out — so its result should be read as a lower bound for any library on
the same page.

| | 10 pages | 200 pages | Allocations (200 pg.) |
| --- | ---: | ---: | ---: |
| **Inkwright, canvas** | **1.4 ms** | **27.3 ms** | **13.6 MB** |
| PDFsharp | 3.6 ms | 57.7 ms | 38.1 MB |
| Inkwright, layout engine | 8.5 ms | 204.0 ms | 108.7 MB |

The low-level canvas is twice as fast as PDFsharp with one-third the allocations. The layout engine
costs roughly seven times more than the canvas — it measures every cell, breaks lines, flows the table
across pages, and walks the document twice to determine page count. That is the price for not
computing coordinates by hand; when coordinates are already known, the canvas is the right tool.

## Reading

90-page document, 4000 paragraphs.

| | Time | Allocations |
| --- | ---: | ---: |
| **Inkwright, open** | **217 µs** | **188 KB** |
| PdfPig, open | 724 µs | 485 KB |
| **Inkwright, extract text** | **93 ms** | **116 MB** |
| PdfPig, extract text | 135 ms | 103 MB |
| **Inkwright, rewrite** | **717 µs** | **439 KB** |

Opening is three times faster with 2.6× less memory: only the cross-reference table is read; objects
are parsed on access.

Extraction is 1.5× faster and slightly more memory-hungry — letters are returned with coordinates,
font size, and font name, not as a single flat string.

Rewriting a 90-page file in 0.7 ms is not magic but a consequence of objects the model never parsed
being copied byte for byte.

## Corpus

Regressions are caught not by microbenchmarks but by a corpus: 7807 files from library test resources
and fuzzing sets are opened, rewritten, and read again.

| | |
| --- | ---: |
| Files in corpus | 7807 |
| Opened successfully | 7800 |
| Rejected by parser (intentionally corrupted fixtures) | 5 |
| Empty files (parser not invoked) | 2 |
| Round-trip failures among files allowed to rewrite | 0 |
| Page count mismatches | 0 |
| Unhandled exceptions | 0 |
| Total pages | 55 827 |

Of the 250 encrypted files in the corpus, 129 open with an empty or obvious password; the rest are
mostly certificate-encrypted files the library does not support.

A full corpus pass takes about 15 seconds in Release.
