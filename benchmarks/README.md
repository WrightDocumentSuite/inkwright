# Inkwright benchmarks

Latest measured run: **2026-11-09**, **Inkwright 0.1.4**.

- [Results, environment, limitations and reproduction commands](../docs/benchmarks.md)
- [Machine-readable snapshot and source/assembly hashes](../docs/benchmark-results/2026-11-09.json)
- [Raw BenchmarkDotNet reports](../docs/benchmark-results/2026-11-09/)
- [Website charts](https://wrightsuite.com/#benchmarks)

The measured source is [`5909182360cd`](https://github.com/WrightDocumentSuite/inkwright/tree/5909182360cd17275aeac3095f11c9145b8a5959).
The benchmark project is [Inkwright.Benchmarks](Inkwright.Benchmarks/).

The run used a shared Windows Server 2025 host with an Intel Xeon Gold 5412U,
.NET SDK 10.0.403, runtime 10.0.14 and BenchmarkDotNet 0.15.8. Timing tables
include confidence margins; allocations use binary KiB/MiB per operation.
Two additional Inkwright passes are published as controls; use pass 1 for the primary table.
Results from the August i7-8700 run are a different hardware baseline, not
evidence of a code speedup. Follow the report's pinned source and per-class
commands when reproducing; a normal build alone does not refresh measurements.
