using Inkwright.Cos;
using Inkwright.Syntax;

namespace Inkwright.Content;

/// <summary>
/// A parsed content stream. Operands live in one flat array and each operation is a
/// <c>(operator, offset, count)</c> triple, so walking a page costs no allocation per operator —
/// the same shape that lets a page with a hundred thousand path segments stay cheap.
/// </summary>
public sealed class ContentOperations
{
    private readonly PdfValue[] _operands;
    private readonly Entry[] _entries;
    private readonly string[] _unknownNames;

    private ContentOperations(PdfValue[] operands, Entry[] entries, string[] unknownNames)
    {
        _operands = operands;
        _entries = entries;
        _unknownNames = unknownNames;
    }

    /// <summary>The number of operations.</summary>
    public int Count => _entries.Length;

    /// <summary>The operator of an operation.</summary>
    /// <param name="index">The zero-based operation index.</param>
    public PdfOperator GetOperator(int index) => _entries[index].Operator;

    /// <summary>The operands of an operation, in the order they were written.</summary>
    /// <param name="index">The zero-based operation index.</param>
    public ReadOnlySpan<PdfValue> GetOperands(int index)
    {
        Entry entry = _entries[index];
        return _operands.AsSpan(entry.Start, entry.Count);
    }

    /// <summary>The spelling of an operator the library does not know.</summary>
    /// <param name="index">The zero-based operation index.</param>
    public string GetUnknownName(int index)
    {
        Entry entry = _entries[index];
        return entry.Operator == PdfOperator.Unknown && entry.NameIndex >= 0
            ? _unknownNames[entry.NameIndex]
            : PdfOperators.Text(entry.Operator);
    }

    /// <summary>Reads a numeric operand, or zero when the operand is missing or not a number.</summary>
    /// <param name="index">The zero-based operation index.</param>
    /// <param name="operand">Which operand to read.</param>
    public double GetNumber(int index, int operand)
    {
        ReadOnlySpan<PdfValue> operands = GetOperands(index);
        return (uint)operand < (uint)operands.Length && operands[operand].IsNumber ? operands[operand].AsNumber : 0;
    }

    /// <summary>Parses a content stream.</summary>
    /// <param name="content">The decoded operators.</param>
    /// <param name="resolver">Resolves references inside marked-content property lists, when there are any.</param>
    public static ContentOperations Parse(ReadOnlySpan<byte> content, IPdfResolver? resolver = null)
    {
        List<PdfValue> operands = [];
        List<Entry> entries = [];
        List<string> unknownNames = [];

        var parser = new PdfObjectParser(content, 0, resolver);
        int operandStart = 0;

        while (true)
        {
            PdfTokenType token = parser.ReadToken();
            if (token == PdfTokenType.EndOfInput)
                break;

            if (token != PdfTokenType.Keyword)
            {
                // Operands accumulate until an operator consumes them.
                parser.Position = parser.TokenStart;
                PdfValue value = parser.ParseValue();
                operands.Add(value);
                continue;
            }

            ReadOnlySpan<byte> keyword = parser.TokenSpan;

            if (keyword.SequenceEqual("true"u8))
            {
                operands.Add(PdfValue.True);
                continue;
            }

            if (keyword.SequenceEqual("false"u8))
            {
                operands.Add(PdfValue.False);
                continue;
            }

            if (keyword.SequenceEqual("null"u8))
            {
                operands.Add(PdfValue.Null);
                continue;
            }

            PdfOperator op = PdfOperators.Parse(keyword);
            int nameIndex = -1;

            if (op == PdfOperator.Unknown)
            {
                nameIndex = unknownNames.Count;
                unknownNames.Add(System.Text.Encoding.ASCII.GetString(keyword));
            }
            else if (op == PdfOperator.InlineImage)
            {
                int position = parser.Position;
                if (InlineImageReader.TryRead(content, ref position, resolver, out PdfValue image))
                {
                    parser.Position = position;
                    operands.Add(image);
                }
            }

            entries.Add(new Entry(op, operandStart, operands.Count - operandStart, nameIndex));
            operandStart = operands.Count;
        }

        return new ContentOperations([.. operands], [.. entries], [.. unknownNames]);
    }

    private readonly record struct Entry(PdfOperator Operator, int Start, int Count, int NameIndex);
}
