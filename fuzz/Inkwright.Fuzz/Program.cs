using System.Buffers.Binary;
using Inkwright.Diagnostics;
using Inkwright.Fonts.Cff;
using Inkwright.Images;
using Inkwright.Images.Jbig2;
using Inkwright.IO;
using SharpFuzz;

namespace Inkwright.Fuzz;

internal static class Program
{
    private static readonly PdfResourceLimits ReaderLimits = new(
        maxInputBytes: 16 * 1024 * 1024,
        maxDecodedBytesPerStream: 8 * 1024 * 1024,
        maxTotalDecodedBytes: 16 * 1024 * 1024,
        maxTotalDecodeWorkBytes: 32 * 1024 * 1024,
        maxFilterStages: 16,
        maxCrossReferenceEntries: 100_000,
        maxCrossReferenceSections: 256,
        maxObjects: 100_000,
        maxStructuralEntries: 200_000,
        maxParsedValues: 500_000,
        maxNestingDepth: 64);

    private static readonly IReadOnlyDictionary<string, Action<ReadOnlyMemory<byte>>> Targets =
        new Dictionary<string, Action<ReadOnlyMemory<byte>>>(StringComparer.OrdinalIgnoreCase)
        {
            ["reader"] = ReadPdf,
            ["cff"] = ReadCff,
            ["jbig2"] = ReadJbig2,
        };

    private static int Main(string[] args)
    {
        if (args.Length == 0 || !Targets.TryGetValue(args[0], out Action<ReadOnlyMemory<byte>>? target))
        {
            Console.Error.WriteLine("Usage: Inkwright.Fuzz <reader|cff|jbig2> [--replay <input>] ");
            return 2;
        }

        if (args.Length == 3 && args[1].Equals("--replay", StringComparison.OrdinalIgnoreCase))
        {
            target(ReadInput(args[2]));
            return 0;
        }

        if (args.Length != 1)
        {
            Console.Error.WriteLine("Pass either a target alone or '<target> --replay <input>'.");
            return 2;
        }

        Fuzzer.OutOfProcess.Run(stream => target(ReadAll(stream)));
        return 0;
    }

    private static ReadOnlyMemory<byte> ReadInput(string path)
        => File.ReadAllBytes(path);

    private static byte[] ReadAll(Stream input)
    {
        using var buffer = new MemoryStream();
        input.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static void ReadPdf(ReadOnlyMemory<byte> input)
    {
        // Recovery and strict parsing take materially different xref, trailer and page-tree paths.
        // Run both even when one rejects the mutation as ordinary malformed input.
        ReadPdf(input, recover: true);
        ReadPdf(input, recover: false);
    }

    private static void ReadPdf(ReadOnlyMemory<byte> input, bool recover)
    {
        try
        {
            using PdfDocument document = PdfDocument.Load(input.ToArray(), new PdfReadOptions
            {
                Recover = recover,
                ResourceLimits = ReaderLimits,
            });

            // Force lazy page, stream, text, image and structure paths rather than stopping after
            // the trailer and catalog have parsed.
            foreach (PdfPage page in document.Pages.Take(32))
            {
                _ = page.GetContent();
                _ = page.ExtractText();
                foreach (PdfPageImage image in page.Images.Take(16))
                    _ = image.Decode();
            }
        }
        catch (Exception exception) when (exception is
            PdfFormatException or
            PdfPasswordException or
            PdfTamperedException or
            PdfResourceLimitException or
            NotSupportedException)
        {
            // Rejected malformed/unsupported input is an expected fuzz outcome. Any other
            // exception reaches the engine as a crash and becomes a regression seed.
        }
    }

    private static void ReadCff(ReadOnlyMemory<byte> input)
    {
        if (CffFont.Read(input) is not { } font)
            return;

        // Parsing the indexes is only the shallow edge of CFF handling. Exercise the charset,
        // charstring/subroutine walk, rebuild and second parse used by real font embedding.
        _ = CffCharset.Read(font);
        ushort last = (ushort)Math.Max(0, font.GlyphCount - 1);
        byte[] subset = CffSubsetter.Subset(font, [0, last], out _);
        _ = CffFont.Read(subset);
    }

    private static void ReadJbig2(ReadOnlyMemory<byte> input)
    {
        if (input.Length < 4)
            return;

        ReadOnlySpan<byte> header = input.Span[..4];
        int width = 1 + (BinaryPrimitives.ReadUInt16BigEndian(header) % 1024);
        int height = 1 + (BinaryPrimitives.ReadUInt16BigEndian(header[2..]) % 1024);

        try
        {
            _ = Jbig2Decoder.Decode(input[4..], ReadOnlyMemory<byte>.Empty, width, height);
        }
        catch (NotSupportedException)
        {
            // The decoder deliberately uses this exception for truncated and unsupported JBIG2
            // syntax. Arithmetic, indexing and allocation failures remain visible to the fuzzer.
        }
    }
}
