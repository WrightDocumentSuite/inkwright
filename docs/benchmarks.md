# Benchmarks

```bash
dotnet run -c Release --project benchmarks/Inkwright.Benchmarks -- --filter '*'
```

Machine: Intel Core i7-8700, Windows Server 2022, .NET 10.0.10, BenchmarkDotNet 0.15.8. The numbers
below are a historical snapshot from a full run at the released commit, cross-checked against two
further runs on the same machine; allocation figures reproduce to the byte, timings within the
stated deviations. The machine hosts other workloads, so treat absolute times as an upper bound and
the ratios as durable only for the package versions named below.

Comparison in this snapshot is against [PDFsharp 6.2.1](https://www.nuget.org/packages/PDFsharp/6.2.1) and
[PdfPig 0.1.11](https://www.nuget.org/packages/PdfPig/0.1.11) — the only PDF libraries for .NET that can be
benchmarked side by side without native dependencies or AGPL-licensed code. QuestPDF is not included: it
pulls in ~50 MB of native Skia per RID, and measuring managed code against it is unfair both ways.

The benchmark project currently pins PDFsharp 6.2.4 and PdfPig 0.1.15. Do not relabel the table with
those versions until the full run plus two cross-check runs have been repeated on the same host.

## Report generation

Table with 40 rows per page, repeating header, footer with page numbers. PDFsharp receives pre-placed
drawing calls — nothing to lay out — so its result should be read as a lower bound for any library on
the same page.

| | 10 pages | 200 pages | Allocations (200 pg.) |
| --- | ---: | ---: | ---: |
| **Inkwright, canvas** | **2.4 ms** | **46 ms** | **13.6 MB** |
| PDFsharp | 6.0 ms | 177 ms¹ | 38.4 MB |
| Inkwright, layout engine | 15.5 ms | 345 ms | 110.3 MB |

¹ PDFsharp's 200-page mean drifts between runs (92–179 ms, StdDev up to ±60 ms within a single
run); its 10-page result is stable. The claim that survives every run is the conservative one below.

The low-level canvas is at least twice as fast as PDFsharp — 2.5× on the stable 10-page workload —
with roughly a third of the allocations. The layout engine costs about seven times more than the
canvas: it measures every cell, breaks lines, flows the table across pages, and walks the document
twice to determine page count. That is the price for not computing coordinates by hand; when
coordinates are already known, the canvas is the right tool.

## Reading

90-page document, 4000 paragraphs.

| | Time | Allocations |
| --- | ---: | ---: |
| **Inkwright, open** | **382 µs** | **188 KB** |
| PdfPig, open | 1,048 µs | 485 KB |
| **Inkwright, extract text** | **177 ms** | **155 MB** |
| PdfPig, extract text | 191 ms | 103 MB |
| **Inkwright, rewrite** | **1.2 ms** | **441 KB** |

Opening is almost three times faster with 2.6× less memory: only the cross-reference table is read;
objects are parsed on access.

Extraction comes out slightly ahead of PdfPig on time and allocates half again as much — letters are
returned with coordinates, font size, and font name, not as a single flat string.

Rewriting a 90-page file in about a millisecond is not magic but a consequence of objects the model
never parsed being copied byte for byte.

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
| Total pages | 57 249 |

Of the 275 encrypted files in the corpus, 126 open with an empty or an obvious password; the rest
want passwords the corpus does not ship, and a handful are certificate-encrypted.

A full corpus pass takes about 20 seconds in Release.
