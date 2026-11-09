# Benchmarks

Measured on **2026-11-09**, Inkwright **0.1.4**, [public source `5909182360cd`](https://github.com/WrightDocumentSuite/inkwright/tree/5909182360cd17275aeac3095f11c9145b8a5959).

Environment: Microsoft Windows Server 2025 Datacenter (build 26100), Intel Xeon Gold 5412U, 24 cores / 48 logical processors, 255.7 GiB RAM; .NET SDK 10.0.403, .NET 10.0.14 (10.0.14, 10.0.1426.52326), BenchmarkDotNet 0.15.8.

The server also ran other workloads. Suites and benchmark classes ran sequentially with their committed job parameters. These are shared-host measurements; the August i7-8700 results are a different hardware baseline and do not establish a code speedup.

Tables use the first complete pass throughout. Timings and allocations are per operation; KiB and MiB are binary units. Error is BenchmarkDotNet's reported confidence margin, N is its retained sample count, and CV is standard deviation divided by mean. High-CV rows and confidence margins larger than the mean should be treated as noisy observations.

The [machine-readable snapshot](benchmark-results/2026-11-09.json) records exact assembly versions, hashes, source commit, and every measured row. Runtime servicing and competitor versions are part of this snapshot, rather than labels copied from the previous tables.

| Assembly | Product version |
| --- | --- |
| `BenchmarkDotNet.Annotations.dll` | `0.15.8+1e8238ef6c08e18821edae83ac8d6b0a2bb1bb98` |
| `BenchmarkDotNet.dll` | `0.15.8+1e8238ef6c08e18821edae83ac8d6b0a2bb1bb98` |
| `Inkwright.Benchmarks.dll` | `0.1.4+5909182360cd17275aeac3095f11c9145b8a5959` |
| `Inkwright.dll` | `0.1.4+5909182360cd17275aeac3095f11c9145b8a5959` |
| `Inkwright.Layout.dll` | `0.1.4+5909182360cd17275aeac3095f11c9145b8a5959` |
| `PdfSharp.BarCodes.dll` | `6.2.4+362d80539100d2d5bba04d58dd13ae14e12cd780` |
| `PdfSharp.Charting.dll` | `6.2.4+362d80539100d2d5bba04d58dd13ae14e12cd780` |
| `PdfSharp.Cryptography.dll` | `6.2.4+362d80539100d2d5bba04d58dd13ae14e12cd780` |
| `PdfSharp.dll` | `6.2.4+362d80539100d2d5bba04d58dd13ae14e12cd780` |
| `PdfSharp.Quality.dll` | `6.2.4+362d80539100d2d5bba04d58dd13ae14e12cd780` |
| `PdfSharp.Shared.dll` | `6.2.4+362d80539100d2d5bba04d58dd13ae14e12cd780` |
| `PdfSharp.Snippets.dll` | `6.2.4+362d80539100d2d5bba04d58dd13ae14e12cd780` |
| `PdfSharp.System.dll` | `6.2.4+362d80539100d2d5bba04d58dd13ae14e12cd780` |
| `PdfSharp.WPFonts.dll` | `6.2.4+362d80539100d2d5bba04d58dd13ae14e12cd780` |
| `UglyToad.PdfPig.Core.dll` | `0.1.15+f131f642976936e06ee91cb19d3ed728f9dd18b6` |
| `UglyToad.PdfPig.dll` | `0.1.15+f131f642976936e06ee91cb19d3ed728f9dd18b6` |
| `UglyToad.PdfPig.DocumentLayoutAnalysis.dll` | `0.1.15+f131f642976936e06ee91cb19d3ed728f9dd18b6` |
| `UglyToad.PdfPig.Fonts.dll` | `0.1.15+f131f642976936e06ee91cb19d3ed728f9dd18b6` |
| `UglyToad.PdfPig.Package.dll` | `0.1.15+f131f642976936e06ee91cb19d3ed728f9dd18b6` |
| `UglyToad.PdfPig.Tokenization.dll` | `0.1.15+f131f642976936e06ee91cb19d3ed728f9dd18b6` |
| `UglyToad.PdfPig.Tokens.dll` | `0.1.15+f131f642976936e06ee91cb19d3ed728f9dd18b6` |

Generation uses 40 rows per requested page. PDFsharp receives pre-positioned drawing calls, while `Inkwright` performs layout and `InkwrightCanvas` draws directly. The reading input is generated from 4,000 paragraphs and is shared by both readers. `InkwrightExtract` calls `page.ExtractText()`, while `PdfPigExtract` reads `page.Text`; the benchmark measures each library's own text API.

## GenerationBenchmarks

| Method | Parameters | Mean | Error | Median | Allocated | N | CV |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Inkwright | Pages=10 | 6.389 ms | 206.133 µs | 6.388 ms | 5.449 MiB | 4 | 0.5% |
| InkwrightCanvas | Pages=10 | 1.128 ms | 209.316 µs | 1.126 ms | 730.899 KiB | 4 | 2.9% |
| PdfSharp | Pages=10 | 2.960 ms | 26.277 µs | 2.961 ms | 2.372 MiB | 4 | 0.1% |
| Inkwright | Pages=200 | 160.862 ms | 118.765 ms | 159.050 ms | 106.325 MiB | 4 | 11.4% |
| InkwrightCanvas | Pages=200 | 20.627 ms | 367.133 µs | 20.621 ms | 13.268 MiB | 4 | 0.3% |
| PdfSharp | Pages=200 | 37.538 ms | 3.856 ms | 37.078 ms | 37.166 MiB | 5 | 2.7% |

## ReadingBenchmarks

| Method | Parameters | Mean | Error | Median | Allocated | N | CV |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| InkwrightOpen |  | 207.969 µs | 30.714 µs | 203.847 µs | 198.875 KiB | 5 | 3.8% |
| PdfPigOpen |  | 388.336 µs | 152.642 µs | 407.921 µs | 509.117 KiB | 5 | 10.2% |
| InkwrightExtract |  | 103.443 ms | 7.649 ms | 103.427 ms | 175.504 MiB | 4 | 1.1% |
| PdfPigExtract |  | 109.770 ms | 41.786 ms | 107.969 ms | 124.361 MiB | 4 | 5.9% |
| InkwrightRewrite |  | 487.287 µs | 18.634 µs | 484.286 µs | 455.359 KiB | 5 | 1.0% |

## Two control repeats

Both benchmark classes were repeated twice after the first full pass on the same host. The primary tables above remain pass 1; the values below retain all three means.

| Method | Parameters | Pass 1 mean | Pass 2 mean | Pass 3 mean | Max/min mean |
| --- | --- | ---: | ---: | ---: | ---: |
| Inkwright | Pages=10 | 6.389 ms | 6.519 ms | 6.707 ms | 1.05× |
| InkwrightCanvas | Pages=10 | 1.128 ms | 1.188 ms | 1.036 ms | 1.15× |
| PdfSharp | Pages=10 | 2.960 ms | 3.220 ms | 3.063 ms | 1.09× |
| Inkwright | Pages=200 | 160.862 ms | 190.181 ms | 172.736 ms | 1.18× |
| InkwrightCanvas | Pages=200 | 20.627 ms | 22.789 ms | 20.737 ms | 1.10× |
| PdfSharp | Pages=200 | 37.538 ms | 39.682 ms | 34.540 ms | 1.15× |
| InkwrightOpen |  | 207.969 µs | 195.277 µs | 189.949 µs | 1.09× |
| PdfPigOpen |  | 388.336 µs | 587.313 µs | 397.662 µs | 1.51× |
| InkwrightExtract |  | 103.443 ms | 100.913 ms | 100.876 ms | 1.03× |
| PdfPigExtract |  | 109.770 ms | 108.892 ms | 107.710 ms | 1.02× |
| InkwrightRewrite |  | 487.287 µs | 560.439 µs | 488.517 µs | 1.15× |

## Reproduce

Use a clean standalone public checkout at the source commit above, the pinned SDK, and the required fonts. Run each class in a separate process with a distinct artifact directory; this avoids shared generated-build cleanup between benchmark sessions.

```sh
dotnet restore benchmarks/Inkwright.Benchmarks/Inkwright.Benchmarks.csproj --locked-mode
dotnet build benchmarks/Inkwright.Benchmarks/Inkwright.Benchmarks.csproj -c Release --no-restore
dotnet run -c Release --project benchmarks/Inkwright.Benchmarks/Inkwright.Benchmarks.csproj --no-build -- --filter "Inkwright.Benchmarks.GenerationBenchmarks.*" --exporters json --artifacts "BenchmarkDotNet.Artifacts/GenerationBenchmarks"
dotnet run -c Release --project benchmarks/Inkwright.Benchmarks/Inkwright.Benchmarks.csproj --no-build -- --filter "Inkwright.Benchmarks.ReadingBenchmarks.*" --exporters json --artifacts "BenchmarkDotNet.Artifacts/ReadingBenchmarks"
```

For Inkwright, run the complete pair of classes three times using pass-specific artifact directories to reproduce the control repeats.

## Historical results and correctness

The [previous benchmark document](https://github.com/WrightDocumentSuite/inkwright/blob/5909182360cd17275aeac3095f11c9145b8a5959/docs/benchmarks.md) preserves the August measurements and their correctness/corpus notes. Corpus snapshots and build/test wall-clock telemetry are separate evidence from the measurements above.
