using System.Globalization;
using Inkwright.Cos;

namespace Inkwright.Functions;

/// <summary>
/// A type 4 function (§7.10.5): a small program in a calculator language, written in a subset of
/// PostScript. The program works on a stack, and every operator of Table 42 is implemented here.
/// </summary>
/// <remarks>
/// The trigonometry works in degrees, as Table 42 requires: <c>sin</c> and <c>cos</c> take an angle
/// in degrees, and <c>atan</c> returns one between 0 and 360.
/// </remarks>
public sealed class PostScriptFunction : PdfFunction
{
    /// <summary>How many values the stack may hold, which the specification puts at a hundred.</summary>
    private const int MaxStack = 100;

    /// <summary>How many operators one evaluation may run, to stop a pathological program dead.</summary>
    private const int MaxSteps = 100_000;

    private readonly PostScriptProgram _program;
    private readonly int _outputs;

    private PostScriptFunction(double[] domain, double[] range, PostScriptProgram program)
        : base(domain, range)
    {
        _program = program;
        _outputs = range.Length / 2;
    }

    /// <inheritdoc />
    public override int OutputCount => _outputs;

    /// <inheritdoc />
    public override void Evaluate(ReadOnlySpan<double> inputs, Span<double> outputs)
    {
        Span<double> stack = stackalloc double[MaxStack];
        int count = 0;

        for (int i = 0; i < InputCount && count < MaxStack; i++)
        {
            double value = i < inputs.Length ? inputs[i] : 0;
            stack[count++] = Clamp(value, Domain[2 * i], Domain[(2 * i) + 1]);
        }

        int steps = MaxSteps;
        _program.Run(stack, ref count, ref steps);

        // The results are the last n values on the stack, bottom to top.
        int available = Math.Min(_outputs, count);
        int start = count - available;

        outputs[..Math.Min(outputs.Length, _outputs)].Clear();
        for (int i = 0; i < available && i < outputs.Length; i++)
            outputs[i] = stack[start + i];

        ClampToRange(outputs);
    }

    internal static PostScriptFunction? Read(PdfStream stream, double[] domain, double[] range) =>
        PostScriptProgram.Parse(stream.DecodedSpan()) is { } program
            ? new PostScriptFunction(domain, range, program)
            : null;
}

/// <summary>
/// A parsed calculator program: a flat list of steps, where a step is either a number to push, an
/// operator to run, or a block of steps that <c>if</c> and <c>ifelse</c> choose between.
/// </summary>
internal sealed class PostScriptProgram
{
    /// <summary>How deeply braces may nest before the program is refused.</summary>
    private const int MaxDepth = 32;

    private readonly List<Step> _steps = [];

    private readonly record struct Step(double Number, string? Operator, PostScriptProgram? Block);

    /// <summary>Parses a program, which the specification wraps in a single pair of braces.</summary>
    /// <param name="source">The program text.</param>
    /// <returns>The program, or <see langword="null"/> when the braces do not balance.</returns>
    internal static PostScriptProgram? Parse(ReadOnlySpan<byte> source)
    {
        int position = 0;

        // The outermost braces wrap the whole program and carry no meaning of their own.
        SkipSpace(source, ref position);
        if (position < source.Length && source[position] == (byte)'{')
            position++;

        return Parse(source, ref position, 0);
    }

    /// <summary>Runs the program over a stack.</summary>
    /// <param name="stack">The working stack.</param>
    /// <param name="count">How many values the stack holds; updated as the program runs.</param>
    /// <param name="steps">How many operators may still be run; counted down.</param>
    internal void Run(Span<double> stack, ref int count, ref int steps)
    {
        for (int i = 0; i < _steps.Count; i++)
        {
            if (--steps < 0)
                return;

            Step step = _steps[i];

            if (step.Block is not null)
            {
                // A block is left for the conditional that follows it; nothing runs it in passing.
                continue;
            }

            if (step.Operator is null)
            {
                if (count < stack.Length)
                    stack[count++] = step.Number;

                continue;
            }

            if (step.Operator is "if" or "ifelse")
            {
                RunConditional(i, step.Operator, stack, ref count, ref steps);
                continue;
            }

            PostScriptOperators.Apply(step.Operator, stack, ref count);
        }
    }

    /// <summary>
    /// Runs a conditional. Its branches are the blocks that sit immediately before it, which is how
    /// the language is written: <c>{ ... } if</c> and <c>{ ... } { ... } ifelse</c>.
    /// </summary>
    private void RunConditional(int index, string @operator, Span<double> stack, ref int count, ref int steps)
    {
        int wanted = @operator == "if" ? 1 : 2;
        if (index < wanted || count < 1)
            return;

        bool taken = stack[--count] != 0;

        PostScriptProgram? branch = @operator == "if"
            ? taken ? _steps[index - 1].Block : null
            : taken ? _steps[index - 2].Block : _steps[index - 1].Block;

        branch?.Run(stack, ref count, ref steps);
    }

    private static PostScriptProgram? Parse(ReadOnlySpan<byte> source, ref int position, int depth)
    {
        if (depth > MaxDepth)
            return null;

        var program = new PostScriptProgram();

        while (true)
        {
            SkipSpace(source, ref position);
            if (position >= source.Length)
                return program;

            byte c = source[position];

            if (c == (byte)'}')
            {
                position++;
                return program;
            }

            if (c == (byte)'{')
            {
                position++;
                if (Parse(source, ref position, depth + 1) is not { } block)
                    return null;

                program._steps.Add(new Step(0, null, block));
                continue;
            }

            int start = position;
            while (position < source.Length && !IsSpace(source[position]) &&
                   source[position] != (byte)'{' && source[position] != (byte)'}')
            {
                position++;
            }

            if (position == start)
                return null;

            ReadOnlySpan<byte> token = source[start..position];

            if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
                program._steps.Add(new Step(number, null, null));
            else
                program._steps.Add(new Step(0, Text(token), null));
        }
    }

    private static string Text(ReadOnlySpan<byte> token)
    {
        Span<char> chars = stackalloc char[token.Length];
        for (int i = 0; i < token.Length; i++)
            chars[i] = (char)token[i];

        return new string(chars);
    }

    private static void SkipSpace(ReadOnlySpan<byte> source, ref int position)
    {
        while (position < source.Length)
        {
            if (IsSpace(source[position]))
            {
                position++;
                continue;
            }

            // A comment runs to the end of the line, as everywhere else in PDF syntax.
            if (source[position] != (byte)'%')
                return;

            while (position < source.Length && source[position] is not ((byte)'\n' or (byte)'\r'))
                position++;
        }
    }

    private static bool IsSpace(byte b) => b is 0x00 or 0x09 or 0x0A or 0x0C or 0x0D or 0x20;
}
