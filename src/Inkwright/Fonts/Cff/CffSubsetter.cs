using System.Buffers.Binary;

namespace Inkwright.Fonts.Cff;

/// <summary>
/// Cuts a Compact Font Format program down to the glyphs a document uses. The outlines of the
/// glyphs left behind are replaced by an empty program, and everything else — the charset, the
/// subroutines, the private data, the font dictionaries of a CID-keyed font — is carried over
/// untouched. Glyph numbers therefore do not move, which is what lets the PDF go on mapping
/// character identifiers to glyphs by identity.
/// </summary>
/// <remarks>
/// This mirrors what the TrueType subsetter does with <c>glyf</c>: the cost of a font is almost
/// entirely its outlines, so emptying them is nearly all of the saving, and it cannot renumber
/// anything by accident.
/// </remarks>
internal static class CffSubsetter
{
    /// <summary>The smallest legal charstring: a single <c>endchar</c>.</summary>
    private static readonly byte[] EmptyCharstring = [0x0E];

    /// <summary>The smallest legal subroutine: a single <c>return</c>.</summary>
    private static readonly byte[] EmptySubroutine = [0x0B];

    /// <summary>Builds a subset of a font program.</summary>
    /// <param name="font">The parsed font.</param>
    /// <param name="glyphs">The glyph indices to keep; glyph zero is always kept.</param>
    /// <param name="kept">Receives every glyph index the returned program still draws.</param>
    /// <returns>A complete font program, or the original bytes when it cannot be rebuilt.</returns>
    internal static byte[] Subset(CffFont font, IReadOnlyCollection<ushort> glyphs, out SortedSet<ushort> kept)
    {
        kept = [0];
        ReadOnlySpan<byte> data = font.Data.Span;

        foreach (ushort glyph in glyphs)
        {
            if (glyph < font.GlyphCount)
                kept.Add(glyph);
        }

        AddComposites(font, kept);

        // Everything the top dictionary points at is copied as one block per structure, in an
        // order this writer chooses, so only the dictionary's own offsets have to be rewritten.
        var blocks = new List<(int Operator, byte[] Bytes)>();

        if (!TryCopyBlock(font, CffFont.CharsetOperator, blocks) ||
            !TryCopyBlock(font, CffFont.FdSelectOperator, blocks))
        {
            return font.Data.ToArray();
        }

        // A glyph-keyed font has one local subroutine index; a CID-keyed one has a private
        // dictionary — and so a local index — per font dictionary, and which glyph uses which is
        // read from the FDSelect structure.
        CffIndex locals = font.IsCidKeyed ? default : FindLocalSubroutines(font);
        int[]? fdSelect = font.IsCidKeyed ? ReadFdSelect(font) : null;
        List<CffIndex> fdLocals = font.IsCidKeyed ? ReadFontDictionaryLocals(font) : [];

        (HashSet<int> usedLocal, HashSet<int> usedGlobal, List<HashSet<int>> usedFdLocal) =
            CollectSubroutines(font, kept, locals, fdSelect, fdLocals);

        byte[] charStrings = BuildCharStrings(font, kept);
        byte[]? privateBlock = CopyPrivate(font, usedLocal, out int privateSize);
        byte[]? fontDictionaries = CopyFontDictionaries(font, fdLocals, usedFdLocal, out List<(int DictSize, byte[] Block)> nestedPrivates);

        return Assemble(
                   font, blocks, charStrings, privateBlock, privateSize,
                   fontDictionaries, nestedPrivates, usedGlobal)
               ?? font.Data.ToArray();
    }

    /// <summary>
    /// Walks every kept glyph to find which subroutines it reaches. Everything else can be emptied
    /// out, which is worth doing because subroutines hold outline fragments and those are the bulk
    /// of a font. A CID-keyed glyph is walked against the local index of its own font dictionary,
    /// so each dictionary's subroutines are trimmed to what its own glyphs need.
    /// </summary>
    private static (HashSet<int> Local, HashSet<int> Global, List<HashSet<int>> FdLocal) CollectSubroutines(
        CffFont font, SortedSet<ushort> kept, CffIndex locals, int[]? fdSelect, List<CffIndex> fdLocals)
    {
        HashSet<int> usedLocal = [];
        HashSet<int> usedGlobal = [];
        List<HashSet<int>> usedFdLocal = [];

        for (int i = 0; i < fdLocals.Count; i++)
            usedFdLocal.Add([]);

        foreach (ushort glyph in kept)
        {
            if (glyph >= font.GlyphCount)
                continue;

            CffIndex glyphLocals = locals;
            HashSet<int> localSet = usedLocal;

            if (fdSelect is not null && fdLocals.Count > 0)
            {
                int fd = glyph < fdSelect.Length ? fdSelect[glyph] : 0;
                if (fd >= 0 && fd < fdLocals.Count)
                {
                    glyphLocals = fdLocals[fd];
                    localSet = usedFdLocal[fd];
                }
            }

            Type2Charstring.CollectSubroutines(
                font.CharStrings.Item(font.Data.Span, glyph),
                glyphLocals,
                font.GlobalSubroutines,
                font.Data,
                localSet,
                usedGlobal);
        }

        return (usedLocal, usedGlobal, usedFdLocal);
    }

    /// <summary>
    /// Reads FDSelect (#5176 §19): the map from glyph to font dictionary. Format 0 lists a font
    /// index per glyph; format 3 lists ranges of glyphs that share one. Anything unrecognised maps
    /// every glyph to the first dictionary, which is the safe default.
    /// </summary>
    private static int[]? ReadFdSelect(CffFont font)
    {
        if (!font.TopDictionary.TryGetValue(CffFont.FdSelectOperator, out double[]? operands) || operands.Length == 0)
            return null;

        int offset = (int)operands[^1];
        ReadOnlySpan<byte> data = font.Data.Span;

        if (offset <= 0 || offset >= data.Length)
            return null;

        int glyphs = font.GlyphCount;
        int[] map = new int[glyphs];
        int format = data[offset];

        if (format == 0)
        {
            for (int i = 0; i < glyphs && offset + 1 + i < data.Length; i++)
                map[i] = data[offset + 1 + i];

            return map;
        }

        if (format != 3 || offset + 3 > data.Length)
            return map;

        int ranges = BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 1)..]);
        int position = offset + 3;

        for (int r = 0; r < ranges && position + 5 <= data.Length; r++)
        {
            int first = BinaryPrimitives.ReadUInt16BigEndian(data[position..]);
            int fd = data[position + 2];
            int next = BinaryPrimitives.ReadUInt16BigEndian(data[(position + 3)..]);

            for (int g = first; g < next && g < glyphs; g++)
                map[g] = fd;

            position += 3;
        }

        return map;
    }

    /// <summary>Reads the local subroutine index of each font dictionary of a CID-keyed font.</summary>
    private static List<CffIndex> ReadFontDictionaryLocals(CffFont font)
    {
        List<CffIndex> locals = [];

        if (!font.TopDictionary.TryGetValue(CffFont.FdArrayOperator, out double[]? operands) || operands.Length == 0)
            return locals;

        int offset = (int)operands[^1];
        int cursor = offset;

        if (offset <= 0 || offset >= font.Data.Length ||
            !CffFont.TryReadIndex(font.Data.Span, ref cursor, out CffIndex array))
        {
            return locals;
        }

        for (int i = 0; i < array.Count; i++)
            locals.Add(LocalSubroutinesOf(font, CffFont.ReadDictionary(array.Item(font.Data.Span, i))));

        return locals;
    }

    /// <summary>Finds the local subroutine index a private dictionary points at, if any.</summary>
    private static CffIndex LocalSubroutinesOf(CffFont font, Dictionary<int, double[]> fontDictionary)
    {
        if (!fontDictionary.TryGetValue(CffFont.PrivateOperator, out double[]? priv) || priv.Length < 2)
            return default;

        int size = (int)priv[0];
        int start = (int)priv[1];

        if (size <= 0 || start <= 0 || start + size > font.Data.Length)
            return default;

        Dictionary<int, double[]> dictionary = CffFont.ReadDictionary(font.Data.Span.Slice(start, size));

        if (!dictionary.TryGetValue(19, out double[]? subrs) || subrs.Length == 0)
            return default;

        int local = start + (int)subrs[^1];
        int cursor = local;

        return local > start && local < font.Data.Length &&
               CffFont.TryReadIndex(font.Data.Span, ref cursor, out CffIndex index)
            ? index
            : default;
    }

    /// <summary>Finds the local subroutines the private dictionary points at, if any.</summary>
    private static CffIndex FindLocalSubroutines(CffFont font)
    {
        if (!font.TopDictionary.TryGetValue(CffFont.PrivateOperator, out double[]? operands) || operands.Length < 2)
            return default;

        int size = (int)operands[0];
        int offset = (int)operands[1];

        if (size <= 0 || offset <= 0 || offset + size > font.Data.Length)
            return default;

        Dictionary<int, double[]> priv = CffFont.ReadDictionary(font.Data.Span.Slice(offset, size));

        if (!priv.TryGetValue(19, out double[]? subrs) || subrs.Length == 0)
            return default;

        int start = offset + (int)subrs[^1];
        int cursor = start;

        return start > offset && start < font.Data.Length &&
               CffFont.TryReadIndex(font.Data.Span, ref cursor, out CffIndex index)
            ? index
            : default;
    }

    /// <summary>
    /// Rebuilds a subroutine index, replacing the ones nothing reaches with a bare <c>return</c>.
    /// The count stays the same on purpose: the numbering is biased by the count, so dropping an
    /// entry would renumber every subroutine after it.
    /// </summary>
    private static byte[] TrimSubroutines(ReadOnlyMemory<byte> data, CffIndex index, HashSet<int> used)
    {
        var items = new List<byte[]>(index.Count);

        for (int i = 0; i < index.Count; i++)
            items.Add(used.Contains(i) ? index.Item(data.Span, i).ToArray() : EmptySubroutine);

        return WriteIndex(items);
    }

    /// <summary>
    /// Adds the glyphs that a kept glyph is built out of. An accented letter may be drawn as a base
    /// letter plus an accent, and the composite names those two by their codes in the standard
    /// encoding, so the codes are turned into names and the names looked up in the charset.
    /// </summary>
    private static void AddComposites(CffFont font, SortedSet<ushort> kept)
    {
        ReadOnlySpan<byte> data = font.Data.Span;
        CffCharset? charset = CffCharset.Read(font);

        if (charset is null)
            return;

        // A part may itself be a composite, so the search runs until nothing new turns up.
        var pending = new Queue<ushort>(kept);

        while (pending.Count > 0)
        {
            ushort glyph = pending.Dequeue();

            if (glyph >= font.GlyphCount)
                continue;

            if (!Type2Charstring.TryReadComposite(font.CharStrings.Item(data, glyph), out int baseCode, out int accentCode))
                continue;

            foreach (int code in new[] { baseCode, accentCode })
            {
                int component = Resolve(charset, code);

                if (component >= 0 && component < font.GlyphCount && kept.Add((ushort)component))
                    pending.Enqueue((ushort)component);
            }
        }
    }

    /// <summary>Turns a standard encoding code into the glyph the font gives that name to.</summary>
    private static int Resolve(CffCharset charset, int code)
    {
        string name = CffStandardEncoding.NameOf(code);
        if (name.Length == 0)
            return -1;

        int identifier = CffCharset.StandardIdentifier(name);
        return identifier < 0 ? -1 : charset.GlyphFor(identifier);
    }

    /// <summary>Rebuilds the charstring INDEX, emptying the glyphs that were not asked for.</summary>
    private static byte[] BuildCharStrings(CffFont font, SortedSet<ushort> kept)
    {
        ReadOnlySpan<byte> data = font.Data.Span;
        var items = new List<byte[]>(font.GlyphCount);

        for (int i = 0; i < font.GlyphCount; i++)
        {
            items.Add(kept.Contains((ushort)i)
                ? font.CharStrings.Item(data, i).ToArray()
                : EmptyCharstring);
        }

        return WriteIndex(items);
    }

    private static bool TryCopyBlock(CffFont font, int @operator, List<(int Operator, byte[] Bytes)> blocks)
    {
        if (!font.TopDictionary.TryGetValue(@operator, out double[]? operands) || operands.Length == 0)
            return true;

        int offset = (int)operands[^1];

        // Values below three name one of the predefined charsets rather than an offset.
        if (@operator == CffFont.CharsetOperator && offset is >= 0 and <= 2)
            return true;

        if (offset <= 0 || offset >= font.Data.Length)
            return true;

        int length = MeasureBlock(font, @operator, offset);
        if (length <= 0 || offset + length > font.Data.Length)
            return false;

        blocks.Add((@operator, font.Data.Span.Slice(offset, length).ToArray()));
        return true;
    }

    /// <summary>
    /// How long a structure is. The format states no length for the charset or the glyph-to-font
    /// mapping, so each is measured from its own shape.
    /// </summary>
    private static int MeasureBlock(CffFont font, int @operator, int offset)
    {
        ReadOnlySpan<byte> data = font.Data.Span;

        return @operator switch
        {
            CffFont.CharsetOperator => MeasureCharset(data, offset, font.GlyphCount),
            CffFont.FdSelectOperator => MeasureFdSelect(data, offset, font.GlyphCount),
            _ => 0,
        };
    }

    private static int MeasureCharset(ReadOnlySpan<byte> data, int offset, int glyphs)
    {
        if (offset >= data.Length)
            return 0;

        int format = data[offset];

        return format switch
        {
            // One name per glyph, two bytes each, the first glyph being implicit.
            0 => 1 + ((glyphs - 1) * 2),

            // Ranges of consecutive names: a first name and a count of how many follow.
            1 => MeasureRanges(data, offset, glyphs, 3),
            2 => MeasureRanges(data, offset, glyphs, 4),
            _ => 0,
        };
    }

    private static int MeasureRanges(ReadOnlySpan<byte> data, int offset, int glyphs, int size)
    {
        int position = offset + 1;
        int covered = 1;

        while (covered < glyphs && position + size <= data.Length)
        {
            int left = size == 3 ? data[position + 2] : BinaryPrimitives.ReadUInt16BigEndian(data[(position + 2)..]);
            covered += left + 1;
            position += size;
        }

        return position - offset;
    }

    private static int MeasureFdSelect(ReadOnlySpan<byte> data, int offset, int glyphs)
    {
        if (offset >= data.Length)
            return 0;

        return data[offset] switch
        {
            // One font index per glyph.
            0 => 1 + glyphs,

            // A run-length list, ended by a sentinel giving the glyph count.
            3 when offset + 3 <= data.Length =>
                3 + (BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 1)..]) * 3) + 2,

            _ => 0,
        };
    }

    /// <summary>
    /// Copies the private dictionary together with the local subroutines that follow it. The
    /// dictionary's own pointer to those subroutines counts from the dictionary's start, so keeping
    /// the two adjacent keeps that pointer correct without rewriting it.
    /// </summary>
    private static byte[]? CopyPrivate(CffFont font, HashSet<int> usedLocal, out int privateSize)
    {
        privateSize = 0;

        if (!font.TopDictionary.TryGetValue(CffFont.PrivateOperator, out double[]? operands) || operands.Length < 2)
            return null;

        int size = (int)operands[0];
        int offset = (int)operands[1];

        if (size <= 0 || offset <= 0 || offset + size > font.Data.Length)
            return null;

        privateSize = size;

        Dictionary<int, double[]> priv = CffFont.ReadDictionary(font.Data.Span.Slice(offset, size));

        if (!priv.TryGetValue(19, out double[]? subrs) || subrs.Length == 0)
            return font.Data.Span.Slice(offset, size).ToArray();

        int local = offset + (int)subrs[^1];
        int cursor = local;

        if (local <= offset || local > font.Data.Length ||
            !CffFont.TryReadIndex(font.Data.Span, ref cursor, out CffIndex index))
        {
            return font.Data.Span.Slice(offset, size).ToArray();
        }

        // The dictionary's pointer to its subroutines counts from the dictionary's own start, so
        // trimming the index is safe as long as the gap in front of it does not change: the index
        // gets shorter, but it still begins where the pointer says.
        byte[] trimmed = TrimSubroutines(font.Data, index, usedLocal);
        int gap = local - offset;

        if (gap < size || local + trimmed.Length > font.Data.Length + trimmed.Length)
            return font.Data.Span.Slice(offset, Math.Max(size, index.End - offset)).ToArray();

        byte[] block = new byte[gap + trimmed.Length];
        font.Data.Span.Slice(offset, gap).CopyTo(block);
        trimmed.CopyTo(block, gap);

        return block;
    }

    /// <summary>
    /// Copies the font dictionaries of a CID-keyed font, each with the private data it names and the
    /// local subroutines that follow it, the unused ones emptied out. The private dictionary's
    /// pointer to its subroutines counts from the dictionary's own start, so keeping the two
    /// adjacent keeps that pointer correct while the index shrinks. The private offsets are
    /// absolute, so they are rewritten once the final layout is known.
    /// </summary>
    private static byte[]? CopyFontDictionaries(
        CffFont font, List<CffIndex> fdLocals, List<HashSet<int>> usedFdLocal, out List<(int DictSize, byte[] Block)> privates)
    {
        privates = [];

        if (!font.TopDictionary.TryGetValue(CffFont.FdArrayOperator, out double[]? operands) || operands.Length == 0)
            return null;

        int offset = (int)operands[^1];
        int cursor = offset;

        if (offset <= 0 || offset >= font.Data.Length ||
            !CffFont.TryReadIndex(font.Data.Span, ref cursor, out CffIndex array))
        {
            return null;
        }

        var dictionaries = new List<byte[]>(array.Count);

        for (int i = 0; i < array.Count; i++)
        {
            Dictionary<int, double[]> dictionary = CffFont.ReadDictionary(array.Item(font.Data.Span, i));

            if (!dictionary.TryGetValue(CffFont.PrivateOperator, out double[]? priv) || priv.Length < 2)
                return null;

            int size = (int)priv[0];
            int start = (int)priv[1];

            if (size <= 0 || start <= 0 || start + size > font.Data.Length)
                return null;

            byte[] block = BuildFontDictionaryPrivate(font.Data, start, size, LocalsOf(fdLocals, i), UsedOf(usedFdLocal, i));
            privates.Add((size, block));

            // The dictionary is rewritten with a placeholder; only the private entry is kept,
            // because nothing else in a font dictionary points anywhere. The size operand is the
            // private dictionary's own, not the block's, and stays fixed at its five-byte width.
            dictionaries.Add(WriteDictionary([(CffFont.PrivateOperator, [size, 0])]));
        }

        return WriteIndex(dictionaries);
    }

    private static CffIndex LocalsOf(List<CffIndex> fdLocals, int index) =>
        index < fdLocals.Count ? fdLocals[index] : default;

    private static HashSet<int> UsedOf(List<HashSet<int>> usedFdLocal, int index) =>
        index < usedFdLocal.Count ? usedFdLocal[index] : [];

    /// <summary>
    /// Builds one font dictionary's private block: the private dictionary itself, then the trimmed
    /// local subroutine index in the place the dictionary's pointer expects it. A present but empty
    /// INDEX is retained as its two-byte zero count; it is distinct from having no Subrs pointer.
    /// </summary>
    internal static byte[] BuildFontDictionaryPrivate(
        ReadOnlyMemory<byte> data, int start, int size, CffIndex locals, HashSet<int> used)
    {
        if (locals.Start <= 0)
            return data.Span.Slice(start, size).ToArray();

        byte[] trimmed = locals.Count == 0
            ? [0, 0]
            : TrimSubroutines(data, locals, used);
        int gap = locals.Start - start;

        if (gap < size || locals.Start >= data.Length)
            return data.Span.Slice(start, size).ToArray();

        byte[] block = new byte[gap + trimmed.Length];
        data.Span.Slice(start, gap).CopyTo(block);
        trimmed.CopyTo(block, gap);
        return block;
    }

    /// <summary>
    /// Lays the pieces out and writes the file. Every offset in the top dictionary is written in
    /// the five-byte form, so the dictionary is the same size whatever the offsets turn out to be
    /// and the layout can be computed before the values are known.
    /// </summary>
    private static byte[]? Assemble(
        CffFont font,
        List<(int Operator, byte[] Bytes)> blocks,
        byte[] charStrings,
        byte[]? privateBlock,
        int privateSize,
        byte[]? fontDictionaries,
        List<(int DictSize, byte[] Block)> nestedPrivates,
        HashSet<int> usedGlobal)
    {
        ReadOnlySpan<byte> data = font.Data.Span;

        byte[] names = data[font.Names.Start..font.Names.End].ToArray();
        byte[] strings = data[font.Strings.Start..font.Strings.End].ToArray();

        // Nothing points at the global subroutines by offset — they are found by their position in
        // the header — so the index can be trimmed freely as long as the count is preserved.
        byte[] global = TrimSubroutines(font.Data, font.GlobalSubroutines, usedGlobal);

        // Two passes: the first learns where everything lands, the second writes the real offsets.
        var placement = new Dictionary<int, int>();
        byte[]? output = null;

        for (int pass = 0; pass < 2; pass++)
        {
            List<(int Operator, double[] Operands)> top = BuildTopDictionary(font, placement, privateSize);
            byte[] topDictionary = WriteIndex([WriteDictionary(top)]);

            using var buffer = new MemoryStream(charStrings.Length + 4096);
            buffer.Write([1, 0, 4, 4]);
            buffer.Write(names);
            buffer.Write(topDictionary);
            buffer.Write(strings);
            buffer.Write(global);

            foreach ((int @operator, byte[] bytes) in blocks)
            {
                placement[@operator] = (int)buffer.Length;
                buffer.Write(bytes);
            }

            placement[CffFont.CharStringsOperator] = (int)buffer.Length;
            buffer.Write(charStrings);

            if (fontDictionaries is not null)
            {
                placement[CffFont.FdArrayOperator] = (int)buffer.Length;

                // The font dictionaries are rewritten in place once their private data is placed.
                long arrayStart = buffer.Length;
                buffer.Write(fontDictionaries);

                var starts = new List<int>(nestedPrivates.Count);
                foreach ((int _, byte[] priv) in nestedPrivates)
                {
                    starts.Add((int)buffer.Length);
                    buffer.Write(priv);
                }

                byte[] rebuilt = RewriteFontDictionaries(nestedPrivates, starts);
                if (rebuilt.Length != fontDictionaries.Length)
                    return null;

                byte[] all = buffer.ToArray();
                rebuilt.CopyTo(all, (int)arrayStart);
                output = all;
                continue;
            }

            if (privateBlock is not null)
            {
                placement[CffFont.PrivateOperator] = (int)buffer.Length;
                buffer.Write(privateBlock);
            }

            output = buffer.ToArray();
        }

        return output;
    }

    private static List<(int Operator, double[] Operands)> BuildTopDictionary(
        CffFont font, Dictionary<int, int> placement, int privateSize)
    {
        var top = new List<(int, double[])>();

        foreach ((int @operator, double[] operands) in font.TopDictionary.OrderBy(entry => entry.Key))
        {
            switch (@operator)
            {
                case CffFont.EncodingOperator:
                    // A subset keeps the standard encoding; a custom one would name glyphs that
                    // are no longer drawn.
                    break;

                case CffFont.CharsetOperator when operands.Length > 0 && operands[^1] is >= 0 and <= 2:
                    top.Add((@operator, operands));
                    break;

                case CffFont.CharsetOperator or CffFont.FdSelectOperator or
                     CffFont.CharStringsOperator or CffFont.FdArrayOperator:
                    top.Add((@operator, [placement.GetValueOrDefault(@operator)]));
                    break;

                case CffFont.PrivateOperator:
                    top.Add((@operator, [privateSize, placement.GetValueOrDefault(@operator)]));
                    break;

                default:
                    top.Add((@operator, operands));
                    break;
            }
        }

        return top;
    }

    private static byte[] RewriteFontDictionaries(List<(int DictSize, byte[] Block)> privates, List<int> starts)
    {
        var dictionaries = new List<byte[]>(privates.Count);

        // The Private entry names the private dictionary's own size, not the block's: the trimmed
        // local subroutines sit past the dictionary and are reached through its own Subrs pointer.
        for (int i = 0; i < privates.Count; i++)
            dictionaries.Add(WriteDictionary([(CffFont.PrivateOperator, [privates[i].DictSize, starts[i]])]));

        return WriteIndex(dictionaries);
    }

    /// <summary>Writes a dictionary, every integer in the five-byte form so sizes never shift.</summary>
    private static byte[] WriteDictionary(List<(int Operator, double[] Operands)> entries)
    {
        using var buffer = new MemoryStream(64);
        Span<byte> number = stackalloc byte[4];

        foreach ((int @operator, double[] operands) in entries)
        {
            foreach (double operand in operands)
            {
                buffer.WriteByte(29);
                BinaryPrimitives.WriteInt32BigEndian(number, (int)operand);
                buffer.Write(number);
            }

            if (@operator >= 1200)
            {
                buffer.WriteByte(12);
                buffer.WriteByte((byte)(@operator - 1200));
            }
            else
                buffer.WriteByte((byte)@operator);
        }

        return buffer.ToArray();
    }

    private static byte[] WriteIndex(List<byte[]> items)
    {
        if (items.Count == 0)
            return [0, 0];

        int total = items.Sum(item => item.Length);
        int offSize = total + 1 <= 0xFF ? 1 : total + 1 <= 0xFFFF ? 2 : total + 1 <= 0xFFFFFF ? 3 : 4;

        using var buffer = new MemoryStream(total + (items.Count * offSize) + 8);

        Span<byte> count = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(count, (ushort)items.Count);
        buffer.Write(count);
        buffer.WriteByte((byte)offSize);

        int offset = 1;
        WriteOffset(buffer, offset, offSize);

        foreach (byte[] item in items)
        {
            offset += item.Length;
            WriteOffset(buffer, offset, offSize);
        }

        foreach (byte[] item in items)
            buffer.Write(item);

        return buffer.ToArray();
    }

    private static void WriteOffset(Stream buffer, int value, int size)
    {
        for (int i = size - 1; i >= 0; i--)
            buffer.WriteByte((byte)(value >> (i * 8)));
    }
}
