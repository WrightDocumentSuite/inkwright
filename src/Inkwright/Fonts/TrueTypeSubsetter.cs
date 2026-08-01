using System.Buffers.Binary;
using System.Text;

namespace Inkwright.Fonts;

/// <summary>
/// Builds a font program holding only the glyphs a document actually used. Glyph indices are kept
/// exactly where they were, so a CID font can go on mapping identity and nothing downstream has to
/// be renumbered; the glyphs left behind become zero-length entries in <c>loca</c>.
/// </summary>
internal static class TrueTypeSubsetter
{
    private static readonly string[] KeptTables =
        ["cvt ", "fpgm", "glyf", "head", "hhea", "hmtx", "loca", "maxp", "prep", "gasp"];

    /// <summary>What an OpenType file with compact outlines keeps beside the <c>CFF</c> table.</summary>
    private static readonly string[] CompactTables = ["head", "hhea", "hmtx", "maxp", "OS/2", "name", "post", "cmap"];

    /// <summary>Builds a subset containing the given glyphs and everything they reference.</summary>
    /// <param name="font">The font to subset.</param>
    /// <param name="glyphs">The glyph indices to keep; glyph zero is always kept.</param>
    /// <returns>A complete font file, or the original bytes when the outlines cannot be subset.</returns>
    internal static byte[] Subset(TrueTypeFont font, IReadOnlyCollection<ushort> glyphs) =>
        Subset(font, glyphs, out _);

    /// <summary>
    /// Builds a subset and reports which glyphs it ended up with. PDF/A-1 wants the descriptor to
    /// list exactly the glyphs the program defines, and only the subsetter knows that: asking for
    /// one glyph can pull in several more through composite references.
    /// </summary>
    /// <param name="font">The font to subset.</param>
    /// <param name="glyphs">The glyph indices to keep; glyph zero is always kept.</param>
    /// <param name="kept">Receives every glyph index the returned program defines.</param>
    internal static byte[] Subset(TrueTypeFont font, IReadOnlyCollection<ushort> glyphs, out SortedSet<ushort> kept)
    {
        kept = [0];

        if (font.IsCompactFontFormat)
            return SubsetCompact(font, glyphs, out kept);

        if (!font.HasTable("glyf") || !font.HasTable("loca"))
            return Whole(font, out kept);

        SortedSet<ushort> keep = kept;
        foreach (ushort glyph in glyphs)
            AddWithComponents(font, glyph, keep, 0);

        int glyphCount = font.GlyphCount;
        if (glyphCount == 0)
            return Whole(font, out kept);

        ReadOnlySpan<byte> glyf = font.GetTable("glyf");
        var newGlyf = new MemoryStream(glyf.Length / 4);
        int[] offsets = new int[glyphCount + 1];

        for (ushort glyph = 0; glyph < glyphCount; glyph++)
        {
            offsets[glyph] = (int)newGlyf.Length;

            if (!keep.Contains(glyph))
                continue;

            (int offset, int length) = font.GetGlyphRange(glyph);
            if (length == 0 || offset + length > glyf.Length)
                continue;

            newGlyf.Write(glyf.Slice(offset, length));

            // Glyph data is aligned to four bytes so that long offsets stay representable.
            while (newGlyf.Length % 4 != 0)
                newGlyf.WriteByte(0);
        }

        offsets[glyphCount] = (int)newGlyf.Length;

        bool longFormat = offsets[glyphCount] > 0x1FFFF;
        byte[] loca = BuildLocations(offsets, longFormat);
        byte[] head = font.GetTable("head").ToArray();
        if (head.Length >= 52)
            BinaryPrimitives.WriteInt16BigEndian(head.AsSpan(50), longFormat ? (short)1 : (short)0);

        var tables = new List<(string Tag, byte[] Data)>
        {
            ("glyf", newGlyf.ToArray()),
            ("loca", loca),
            ("head", head),
        };

        foreach (string tag in KeptTables)
        {
            if (tag is "glyf" or "loca" or "head" || !font.HasTable(tag))
                continue;

            tables.Add((tag, font.GetTable(tag).ToArray()));
        }

        return Assemble(tables);
    }

    /// <summary>
    /// Subsets a font whose outlines are a Compact Font Format program rather than <c>glyf</c>.
    /// A bare CFF is returned as one, and one wrapped in an OpenType file is rewrapped, because
    /// that is the shape the descriptor's <c>FontFile3</c> subtype has already promised.
    /// </summary>
    private static byte[] SubsetCompact(TrueTypeFont font, IReadOnlyCollection<ushort> glyphs, out SortedSet<ushort> kept)
    {
        ReadOnlyMemory<byte> program = font.GetTableRange("CFF ") is (int offset, int length) && length > 0
            ? font.Data.Slice(offset, length)
            : font.Data;

        Cff.CffFont? cff = Cff.CffFont.Read(program);
        if (cff is null || cff.GlyphCount == 0)
            return Whole(font, out kept);

        byte[] subset = Cff.CffSubsetter.Subset(cff, glyphs, out kept);

        // A program that did not actually shrink is not worth the risk of having rebuilt it.
        if (subset.Length >= program.Length)
            return Whole(font, out kept);

        if (!font.HasTable("CFF "))
            return subset;

        var tables = new List<(string Tag, byte[] Data)> { ("CFF ", subset) };

        foreach (string tag in CompactTables)
        {
            if (font.HasTable(tag))
                tables.Add((tag, font.GetTable(tag).ToArray()));
        }

        return Assemble(tables);
    }

    /// <summary>The original program, whose glyphs are all of them.</summary>
    private static byte[] Whole(TrueTypeFont font, out SortedSet<ushort> kept)
    {
        kept = [];
        for (ushort glyph = 0; glyph < font.GlyphCount; glyph++)
            kept.Add(glyph);

        return font.Data.ToArray();
    }

    private static void AddWithComponents(TrueTypeFont font, ushort glyph, SortedSet<ushort> keep, int depth)
    {
        if (depth > 8 || glyph >= font.GlyphCount || !keep.Add(glyph))
            return;

        foreach (ushort component in font.GetComponents(glyph))
            AddWithComponents(font, component, keep, depth + 1);
    }

    private static byte[] BuildLocations(int[] offsets, bool longFormat)
    {
        byte[] loca = new byte[offsets.Length * (longFormat ? 4 : 2)];

        for (int i = 0; i < offsets.Length; i++)
        {
            if (longFormat)
                BinaryPrimitives.WriteUInt32BigEndian(loca.AsSpan(i * 4), (uint)offsets[i]);
            else
                BinaryPrimitives.WriteUInt16BigEndian(loca.AsSpan(i * 2), (ushort)(offsets[i] / 2));
        }

        return loca;
    }

    private static byte[] Assemble(List<(string Tag, byte[] Data)> tables)
    {
        tables.Sort((a, b) => string.CompareOrdinal(a.Tag, b.Tag));

        int count = tables.Count;
        int searchRange = 16;
        int entrySelector = 0;
        while (searchRange * 2 <= count * 16)
        {
            searchRange *= 2;
            entrySelector++;
        }

        int directoryLength = 12 + (count * 16);
        int total = directoryLength;
        foreach ((string _, byte[] data) in tables)
            total += Align(data.Length);

        byte[] file = new byte[total];
        BinaryPrimitives.WriteUInt32BigEndian(file, 0x00010000);
        BinaryPrimitives.WriteUInt16BigEndian(file.AsSpan(4), (ushort)count);
        BinaryPrimitives.WriteUInt16BigEndian(file.AsSpan(6), (ushort)searchRange);
        BinaryPrimitives.WriteUInt16BigEndian(file.AsSpan(8), (ushort)entrySelector);
        BinaryPrimitives.WriteUInt16BigEndian(file.AsSpan(10), (ushort)((count * 16) - searchRange));

        int record = 12;
        int payload = directoryLength;

        foreach ((string tag, byte[] data) in tables)
        {
            Encoding.ASCII.GetBytes(tag, file.AsSpan(record, 4));
            BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(record + 4), Checksum(data));
            BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(record + 8), (uint)payload);
            BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(record + 12), (uint)data.Length);

            data.CopyTo(file.AsSpan(payload));
            payload += Align(data.Length);
            record += 16;
        }

        return file;
    }

    private static int Align(int length) => (length + 3) & ~3;

    private static uint Checksum(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        int whole = data.Length / 4 * 4;

        for (int i = 0; i < whole; i += 4)
            sum += BinaryPrimitives.ReadUInt32BigEndian(data[i..]);

        if (whole < data.Length)
        {
            Span<byte> tail = stackalloc byte[4];
            data[whole..].CopyTo(tail);
            sum += BinaryPrimitives.ReadUInt32BigEndian(tail);
        }

        return sum;
    }
}
