using Inkwright.Fonts;
using Inkwright.Fonts.Cff;
using Xunit;

namespace Inkwright.Tests;

/// <summary>
/// Subsetting a Compact Font Format program. The outlines of unused glyphs are emptied while every
/// glyph keeps its number, so a font can be cut down without disturbing the identity mapping a PDF
/// relies on to find glyphs by character identifier.
/// </summary>
public class CffSubsetTests
{
    [Fact]
    public void ReadsTheStructureOfARealProgram()
    {
        CffFont font = Load();

        Assert.True(font.GlyphCount > 100, $"Only {font.GlyphCount} glyphs were found.");
        Assert.True(font.CharStrings.Count == font.GlyphCount);
        Assert.True(font.TopDictionary.ContainsKey(CffFont.CharStringsOperator));

        // Glyph zero is the one drawn when nothing else matches, and it always exists.
        Assert.False(font.CharStrings.Item(font.Data.Span, 0).IsEmpty);
    }

    [Fact]
    public void ShrinksTheProgramAndKeepsGlyphNumbering()
    {
        CffFont font = Load();
        ushort[] wanted = [0, 5, 36, 40, 72];

        byte[] subset = CffSubsetter.Subset(font, wanted, out SortedSet<ushort> kept);

        Assert.True(
            subset.Length < font.Data.Length / 2,
            $"The subset is {subset.Length} bytes against an original of {font.Data.Length}.");

        CffFont? rebuilt = CffFont.Read(subset);
        Assert.NotNull(rebuilt);

        // The glyph count is unchanged, which is what keeps the identity mapping valid.
        Assert.Equal(font.GlyphCount, rebuilt.GlyphCount);

        foreach (ushort glyph in wanted)
        {
            Assert.Contains(glyph, kept);
            Assert.True(
                rebuilt.CharStrings.Item(subset, glyph)
                    .SequenceEqual(font.CharStrings.Item(font.Data.Span, glyph)),
                $"Glyph {glyph} lost its outline.");
        }
    }

    [Fact]
    public void EmptiesTheGlyphsNobodyAskedFor()
    {
        CffFont font = Load();

        byte[] subset = CffSubsetter.Subset(font, [0, 36], out _);
        CffFont? rebuilt = CffFont.Read(subset);
        Assert.NotNull(rebuilt);

        int emptied = 0;
        for (ushort glyph = 1; glyph < rebuilt.GlyphCount; glyph++)
        {
            if (glyph == 36)
                continue;

            ReadOnlySpan<byte> charstring = rebuilt.CharStrings.Item(subset, glyph);

            // The smallest legal charstring is a single endchar.
            if (charstring.Length == 1 && charstring[0] == 0x0E)
                emptied++;
        }

        Assert.True(emptied > rebuilt.GlyphCount - 5, $"Only {emptied} of {rebuilt.GlyphCount} were emptied.");
    }

    [Fact]
    public void KeepsTheDictionariesPointingAtTheRightPlaces()
    {
        CffFont font = Load();
        byte[] subset = CffSubsetter.Subset(font, [0, 10, 20], out _);

        CffFont? rebuilt = CffFont.Read(subset);
        Assert.NotNull(rebuilt);

        // Every offset the top dictionary carries must land inside the new program.
        foreach (int @operator in new[]
                 {
                     CffFont.CharStringsOperator, CffFont.CharsetOperator,
                     CffFont.FdArrayOperator, CffFont.FdSelectOperator,
                 })
        {
            if (!rebuilt.TopDictionary.TryGetValue(@operator, out double[]? operands) || operands.Length == 0)
                continue;

            int offset = (int)operands[^1];

            // A charset below three names a predefined one rather than a place in the file.
            if (@operator == CffFont.CharsetOperator && offset <= 2)
                continue;

            Assert.InRange(offset, 1, subset.Length - 1);
        }

        if (rebuilt.TopDictionary.TryGetValue(CffFont.PrivateOperator, out double[]? priv) && priv.Length >= 2)
        {
            Assert.InRange((int)priv[1], 1, subset.Length - 1);
            Assert.InRange((int)priv[1] + (int)priv[0], 1, subset.Length);
        }
    }

    [Fact]
    public void ResolvesTheStandardEncodingTheWayCompositesNameTheirParts()
    {
        // A composite glyph names its base and accent by code in the standard encoding, so the
        // codes have to turn back into the names the charset uses.
        Assert.Equal("A", CffStandardEncoding.NameOf(65));
        Assert.Equal("space", CffStandardEncoding.NameOf(32));
        Assert.Equal("acute", CffStandardEncoding.NameOf(194));
        Assert.Equal("dieresis", CffStandardEncoding.NameOf(200));
        Assert.Equal("germandbls", CffStandardEncoding.NameOf(251));

        // The encoding leaves gaps, and a gap has to read as nothing rather than as some neighbour.
        Assert.Empty(CffStandardEncoding.NameOf(0));
        Assert.Empty(CffStandardEncoding.NameOf(160));
        Assert.Empty(CffStandardEncoding.NameOf(300));

        Assert.Equal(34, CffCharset.StandardIdentifier("A"));
        Assert.Equal(1, CffCharset.StandardIdentifier("space"));
        Assert.Equal(-1, CffCharset.StandardIdentifier("no such glyph"));
    }

    [Fact]
    public void ReadsTheCharsetOfARealProgram()
    {
        CffFont font = Load();
        CffCharset? charset = CffCharset.Read(font);

        Assert.NotNull(charset);

        // Glyph zero is the one drawn when nothing matches and always carries the first name.
        Assert.Equal(0, charset[0]);

        // Every glyph has to carry some name, or the charset was read short.
        int named = 0;
        for (int glyph = 0; glyph < font.GlyphCount; glyph++)
        {
            if (charset[glyph] >= 0)
                named++;
        }

        Assert.Equal(font.GlyphCount, named);
    }

    [Fact]
    public void KeepsOnlyTheSubroutinesTheKeptGlyphsReach()
    {
        CffFont font = Load();

        byte[] subset = CffSubsetter.Subset(font, [0, 10, 20], out _);
        CffFont? rebuilt = CffFont.Read(subset);
        Assert.NotNull(rebuilt);

        // The count has to survive untouched: subroutine numbers are biased by how many there are,
        // so dropping one would renumber every subroutine after it.
        Assert.Equal(font.GlobalSubroutines.Count, rebuilt.GlobalSubroutines.Count);

        int emptied = 0;
        for (int i = 0; i < rebuilt.GlobalSubroutines.Count; i++)
        {
            ReadOnlySpan<byte> routine = rebuilt.GlobalSubroutines.Item(subset, i);

            // The smallest legal subroutine is a bare return.
            if (routine.Length == 1 && routine[0] == 0x0B)
                emptied++;
        }

        Assert.True(
            font.GlobalSubroutines.Count == 0 || emptied > 0,
            "No global subroutine was emptied, so none of them was being trimmed.");
    }

    [Fact]
    public void TrimsTheLocalSubroutinesOfEachFontDictionaryInACidFont()
    {
        byte[]? program = FindCidProgramWithLocalSubroutines();
        Assert.SkipWhen(program is null, "No CID-keyed compact font program with local subroutines was found.");

        CffFont font = CffFont.Read(program)!;
        (int Fd, int Count)[] before = FontDictionaryLocalCounts(font);

        // Keep glyph zero and a handful spread across the font, so more than one font dictionary
        // is exercised.
        ushort[] wanted = [0, 1, .. Enumerable.Range(1, 9).Select(i => (ushort)(i * (font.GlyphCount / 10)))];
        byte[] subset = CffSubsetter.Subset(font, wanted, out SortedSet<ushort> kept);

        CffFont? rebuilt = CffFont.Read(subset);
        Assert.NotNull(rebuilt);
        Assert.Equal(font.GlyphCount, rebuilt.GlyphCount);
        Assert.True(rebuilt.IsCidKeyed);

        // Every kept glyph keeps its outline byte for byte: the subroutines it calls survived.
        foreach (ushort glyph in wanted.Where(g => g < font.GlyphCount))
        {
            Assert.True(
                rebuilt.CharStrings.Item(subset, glyph).SequenceEqual(font.CharStrings.Item(font.Data.Span, glyph)),
                $"Glyph {glyph} lost its outline.");
        }

        // Each font dictionary keeps its local subroutine count — the numbering is biased by it —
        // and at least one dictionary has had an unused routine emptied to a bare return.
        (int Fd, int Count)[] after = FontDictionaryLocalCounts(rebuilt);
        Assert.Equal(before.Select(entry => entry.Count), after.Select(entry => entry.Count));

        int emptied = CountEmptiedLocalSubroutines(rebuilt, subset);
        int total = before.Sum(entry => entry.Count);
        Assert.True(total == 0 || emptied > 0, "No font-dictionary local subroutine was trimmed.");
    }

    [Fact]
    public void RetainsAPresentButEmptyLocalSubroutineIndex()
    {
        // The private dictionary occupies bytes 2..4 and its Subrs pointer lands at byte 5. An
        // empty INDEX is still two bytes long; omitting it would leave the pointer dangling.
        byte[] source = [9, 9, 10, 11, 12, 0, 0, 8];
        var empty = new CffIndex(Start: 5, End: 7, Offsets: [0]);

        byte[] block = CffSubsetter.BuildFontDictionaryPrivate(source, start: 2, size: 3, empty, []);

        Assert.Equal([10, 11, 12, 0, 0], block);
    }

    /// <summary>The local subroutine count of each font dictionary of a CID-keyed font.</summary>
    private static (int Fd, int Count)[] FontDictionaryLocalCounts(CffFont font)
    {
        List<(int, int)> counts = [];

        if (!font.TopDictionary.TryGetValue(CffFont.FdArrayOperator, out double[]? operands) || operands.Length == 0)
            return [.. counts];

        int cursor = (int)operands[^1];
        if (!CffFont.TryReadIndex(font.Data.Span, ref cursor, out CffIndex array))
            return [.. counts];

        for (int i = 0; i < array.Count; i++)
            counts.Add((i, LocalIndexOf(font, array.Item(font.Data.Span, i)).Count));

        return [.. counts];
    }

    private static int CountEmptiedLocalSubroutines(CffFont font, byte[] data)
    {
        int emptied = 0;

        if (!font.TopDictionary.TryGetValue(CffFont.FdArrayOperator, out double[]? operands) || operands.Length == 0)
            return 0;

        int cursor = (int)operands[^1];
        if (!CffFont.TryReadIndex(font.Data.Span, ref cursor, out CffIndex array))
            return 0;

        for (int i = 0; i < array.Count; i++)
        {
            CffIndex locals = LocalIndexOf(font, array.Item(font.Data.Span, i));
            for (int s = 0; s < locals.Count; s++)
            {
                ReadOnlySpan<byte> routine = locals.Item(data, s);
                if (routine.Length == 1 && routine[0] == 0x0B)
                    emptied++;
            }
        }

        return emptied;
    }

    private static CffIndex LocalIndexOf(CffFont font, ReadOnlySpan<byte> fontDictionary)
    {
        Dictionary<int, double[]> dictionary = CffFont.ReadDictionary(fontDictionary);

        if (!dictionary.TryGetValue(CffFont.PrivateOperator, out double[]? priv) || priv.Length < 2)
            return default;

        int size = (int)priv[0];
        int start = (int)priv[1];
        if (size <= 0 || start <= 0 || start + size > font.Data.Length)
            return default;

        Dictionary<int, double[]> privateDict = CffFont.ReadDictionary(font.Data.Span.Slice(start, size));
        if (!privateDict.TryGetValue(19, out double[]? subrs) || subrs.Length == 0)
            return default;

        int local = start + (int)subrs[^1];
        int cursor = local;
        return local > start && local < font.Data.Length &&
               CffFont.TryReadIndex(font.Data.Span, ref cursor, out CffIndex index)
            ? index
            : default;
    }

    private static byte[]? FindCidProgramWithLocalSubroutines()
    {
        foreach (string path in Corpus.Files.Take(600))
        {
            PdfDocument document;
            try
            {
                document = PdfDocument.Load(File.ReadAllBytes(path), Corpus.ReadOptions);
            }
            catch
            {
                continue;
            }

            using (document)
            {
                foreach (byte[] program in EmbeddedPrograms(document))
                {
                    if (CffFont.Read(program) is { IsCidKeyed: true } candidate &&
                        FontDictionaryLocalCounts(candidate).Any(entry => entry.Count > 0))
                    {
                        return program;
                    }
                }
            }
        }

        return null;
    }


    [Fact]
    public void KeepsASubsetSmallerThanBefore()
    {
        CffFont font = Load();

        byte[] subset = CffSubsetter.Subset(font, [0, 36], out _);

        // Emptying outlines and subroutines together should leave a small fraction of the original.
        Assert.True(
            subset.Length < font.Data.Length / 2,
            $"The subset is {subset.Length} bytes against an original of {font.Data.Length}.");
    }

    [Fact]
    public void LeavesAProgramItCannotRebuildAlone()
    {
        // Anything that is not a compact font program comes back untouched rather than mangled.
        Assert.Null(CffFont.Read(new byte[] { 0x00, 0x01, 0x00, 0x00 }));
        Assert.Null(CffFont.Read(ReadOnlyMemory<byte>.Empty));
    }

    /// <summary>
    /// A compact font program taken from a document in the corpus. Real embedded fonts are the
    /// material this subsetter has to survive, and they are more varied than anything written here
    /// by hand would be.
    /// </summary>
    private static readonly Lazy<byte[]?> Embedded = new(FindEmbeddedProgram);

    private static CffFont Load()
    {
        byte[]? program = Embedded.Value;
        Assert.SkipWhen(program is null, "No embedded compact font program was found in the corpus.");

        CffFont? cff = CffFont.Read(program);
        Assert.NotNull(cff);
        return cff;
    }

    private static byte[]? FindEmbeddedProgram()
    {
        foreach (string path in Corpus.Files.Take(400))
        {
            PdfDocument document;
            try
            {
                document = PdfDocument.Load(File.ReadAllBytes(path), Corpus.ReadOptions);
            }
            catch
            {
                continue;
            }

            using (document)
            {
                foreach (byte[] program in EmbeddedPrograms(document))
                {
                    CffFont? candidate = CffFont.Read(program);

                    // A font worth testing against has enough glyphs that emptying most of them is
                    // a real saving, and private data whose offsets have to be rewritten.
                    if (candidate is { GlyphCount: > 100 } &&
                        (candidate.TopDictionary.ContainsKey(CffFont.PrivateOperator) ||
                         candidate.TopDictionary.ContainsKey(CffFont.FdArrayOperator)))
                    {
                        return program;
                    }
                }
            }
        }

        return null;
    }

    private static IEnumerable<byte[]> EmbeddedPrograms(PdfDocument document)
    {
        Inkwright.Cos.PdfName fontFile = Inkwright.Cos.PdfName.Get("FontFile3");

        for (int number = 1; number < 4000; number++)
        {
            byte[]? program = null;

            try
            {
                if (document.GetObject(number).AsDictionary is not { } dictionary)
                    continue;

                Inkwright.Cos.PdfValue reference = dictionary.Get(fontFile);
                if (reference.IsNull)
                    continue;

                if (document.Resolve(reference).AsStream is { } stream)
                    program = stream.Decode().ToArray();
            }
            catch
            {
                // An object that will not resolve or decode is not a candidate.
            }

            // A compact font program starts with a major version of one.
            if (program is { Length: > 64 } && program[0] == 1)
                yield return program;
        }
    }
}
