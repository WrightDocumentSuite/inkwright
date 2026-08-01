namespace Inkwright.Images.Jbig2;

/// <summary>
/// Supplies the numbers a symbol dictionary or text region is built from. JBIG2 offers two entropy
/// coders for the same fields — an adaptive arithmetic coder and static Huffman tables — and the
/// procedures that consume the numbers are otherwise identical, so the choice is hidden here rather
/// than repeated through both.
/// </summary>
internal abstract class Jbig2ValueReader
{
    /// <summary>Whether values come from Huffman tables rather than the arithmetic coder.</summary>
    internal abstract bool IsHuffman { get; }

    /// <summary>The arithmetic decoder, for the bitmap procedures that need it directly.</summary>
    internal abstract MqDecoder? Arithmetic { get; }

    internal abstract int DeltaHeight();

    internal abstract int DeltaWidth();

    internal abstract int ExportRun();

    internal abstract int AggregateCount();

    internal abstract int DeltaT();

    internal abstract int FirstS();

    internal abstract int DeltaS();

    /// <summary>The offset of an instance within its strip.</summary>
    /// <param name="logStrips">The base-two logarithm of the strip height.</param>
    internal abstract int CurrentT(int logStrips);

    /// <summary>Whether this instance refines its symbol rather than copying it.</summary>
    internal abstract int RefinementFlag();

    internal abstract int RefinementDeltaWidth();

    internal abstract int RefinementDeltaHeight();

    internal abstract int RefinementX();

    internal abstract int RefinementY();

    /// <summary>The size in bytes of a refinement's coded data, which only the Huffman variant states.</summary>
    internal abstract int RefinementSize();

    /// <summary>Reads a symbol identifier.</summary>
    internal abstract int SymbolId();
}

/// <summary>Reads values from the adaptive arithmetic coder (Annex A).</summary>
internal sealed class Jbig2ArithmeticReader : Jbig2ValueReader
{
    private readonly MqDecoder _decoder;
    private readonly Jbig2IntDecoders _integers;
    private Jbig2IdDecoder _ids;

    internal Jbig2ArithmeticReader(MqDecoder decoder, Jbig2IntDecoders integers, Jbig2IdDecoder ids)
    {
        _decoder = decoder;
        _integers = integers;
        _ids = ids;
    }

    internal override bool IsHuffman => false;

    internal override MqDecoder? Arithmetic => _decoder;

    internal override int DeltaHeight() => _integers.DeltaHeight.Decode(_decoder);

    internal override int DeltaWidth() => _integers.DeltaWidth.Decode(_decoder);

    internal override int ExportRun() => _integers.ExportRun.Decode(_decoder);

    internal override int AggregateCount() => _integers.AggregateCount.Decode(_decoder);

    internal override int DeltaT() => _integers.DeltaT.Decode(_decoder);

    internal override int FirstS() => _integers.FirstS.Decode(_decoder);

    internal override int DeltaS() => _integers.DeltaS.Decode(_decoder);

    internal override int CurrentT(int logStrips) =>
        logStrips == 0 ? 0 : _integers.CurrentT.Decode(_decoder);

    internal override int RefinementFlag() => _integers.RefinementFlag.Decode(_decoder);

    internal override int RefinementDeltaWidth() => _integers.RefinementDeltaWidth.Decode(_decoder);

    internal override int RefinementDeltaHeight() => _integers.RefinementDeltaHeight.Decode(_decoder);

    internal override int RefinementX() => _integers.RefinementX.Decode(_decoder);

    internal override int RefinementY() => _integers.RefinementY.Decode(_decoder);

    /// <summary>The arithmetic variant states no size; the coder finds the end itself.</summary>
    internal override int RefinementSize() => 0;

    internal override int SymbolId() => _ids.Decode(_decoder);
}

/// <summary>The Huffman tables a symbol dictionary or text region has selected.</summary>
internal sealed class Jbig2HuffmanTables
{
    internal required Jbig2HuffmanTable DeltaHeight { get; init; }

    internal required Jbig2HuffmanTable DeltaWidth { get; init; }

    internal required Jbig2HuffmanTable BitmapSize { get; init; }

    internal required Jbig2HuffmanTable AggregateCount { get; init; }

    internal Jbig2HuffmanTable? FirstS { get; init; }

    internal Jbig2HuffmanTable? DeltaS { get; init; }

    internal Jbig2HuffmanTable? DeltaT { get; init; }

    internal Jbig2HuffmanTable? RefinementDeltaWidth { get; init; }

    internal Jbig2HuffmanTable? RefinementDeltaHeight { get; init; }

    internal Jbig2HuffmanTable? RefinementX { get; init; }

    internal Jbig2HuffmanTable? RefinementY { get; init; }

    internal Jbig2HuffmanTable? RefinementSize { get; init; }

    internal Jbig2HuffmanTable? SymbolIds { get; init; }
}

/// <summary>Reads values from static Huffman tables (Annex B).</summary>
internal sealed class Jbig2HuffmanReader : Jbig2ValueReader
{
    private readonly Jbig2BitReader _reader;
    private readonly Jbig2HuffmanTables _tables;

    internal Jbig2HuffmanReader(Jbig2BitReader reader, Jbig2HuffmanTables tables)
    {
        _reader = reader;
        _tables = tables;
    }

    internal override bool IsHuffman => true;

    internal override MqDecoder? Arithmetic => null;

    /// <summary>The underlying bit reader, for the fields that are read raw.</summary>
    internal Jbig2BitReader Bits => _reader;

    /// <summary>The size in bytes of a height class collective bitmap (§6.5.9).</summary>
    internal int BitmapSize() => _tables.BitmapSize.Decode(_reader);

    internal override int DeltaHeight() => _tables.DeltaHeight.Decode(_reader);

    internal override int DeltaWidth() => _tables.DeltaWidth.Decode(_reader);

    /// <summary>Export runs always use table B.1, whatever else the dictionary selected.</summary>
    internal override int ExportRun() => Jbig2StandardTables.Get(1).Decode(_reader);

    internal override int AggregateCount() => _tables.AggregateCount.Decode(_reader);

    internal override int DeltaT() => Require(_tables.DeltaT).Decode(_reader);

    internal override int FirstS() => Require(_tables.FirstS).Decode(_reader);

    internal override int DeltaS() => Require(_tables.DeltaS).Decode(_reader);

    /// <summary>In the Huffman variant the offset within a strip is read as plain bits (§6.4.9).</summary>
    internal override int CurrentT(int logStrips) =>
        logStrips == 0 ? 0 : (int)_reader.ReadBits(logStrips);

    internal override int RefinementFlag() => _reader.ReadBit();

    internal override int RefinementDeltaWidth() => Require(_tables.RefinementDeltaWidth).Decode(_reader);

    internal override int RefinementDeltaHeight() => Require(_tables.RefinementDeltaHeight).Decode(_reader);

    internal override int RefinementX() => Require(_tables.RefinementX).Decode(_reader);

    internal override int RefinementY() => Require(_tables.RefinementY).Decode(_reader);

    internal override int RefinementSize() => Require(_tables.RefinementSize).Decode(_reader);

    internal override int SymbolId() => Require(_tables.SymbolIds).Decode(_reader);

    private static Jbig2HuffmanTable Require(Jbig2HuffmanTable? table) =>
        table ?? throw new NotSupportedException("A JBIG2 segment used a Huffman table it never selected.");
}
