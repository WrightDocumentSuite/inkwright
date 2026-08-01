namespace Inkwright.Functions;

/// <summary>
/// The operators of the calculator language, as ISO 32000-1 Table 42 lists them. An operator that
/// finds too few values on the stack does nothing at all, which keeps a damaged program from
/// throwing in the middle of painting a page.
/// </summary>
internal static class PostScriptOperators
{
    /// <summary>Runs one operator against the stack.</summary>
    /// <param name="operator">The operator name.</param>
    /// <param name="stack">The working stack.</param>
    /// <param name="count">How many values the stack holds; updated in place.</param>
    internal static void Apply(string @operator, Span<double> stack, ref int count)
    {
        switch (@operator)
        {
            // Constants.
            case "true":
                Push(stack, ref count, 1);
                return;
            case "false":
                Push(stack, ref count, 0);
                return;

            // One number in, one out.
            case "abs":
                Unary(stack, count, Math.Abs);
                return;
            case "neg":
                Unary(stack, count, static x => -x);
                return;
            case "ceiling":
                Unary(stack, count, Math.Ceiling);
                return;
            case "floor":
                Unary(stack, count, Math.Floor);
                return;
            case "round":
                Unary(stack, count, static x => Math.Round(x, MidpointRounding.AwayFromZero));
                return;
            case "truncate":
                Unary(stack, count, Math.Truncate);
                return;
            case "sqrt":
                Unary(stack, count, static x => Math.Sqrt(Math.Max(0, x)));
                return;
            case "ln":
                Unary(stack, count, static x => x > 0 ? Math.Log(x) : 0);
                return;
            case "cvi":
                Unary(stack, count, static x => (int)x);
                return;
            case "cvr":
                return;
            case "not":
                // The operator is boolean for a boolean and bitwise for an integer, and the two are
                // told apart by nothing but what happens to be on the stack.
                Unary(stack, count, static x => x is 0 or 1 ? 1 - x : ~(int)x);
                return;

            // Trigonometry, which Table 42 states in degrees rather than radians.
            case "sin":
                Unary(stack, count, static x => Math.Sin(double.DegreesToRadians(x)));
                return;
            case "cos":
                Unary(stack, count, static x => Math.Cos(double.DegreesToRadians(x)));
                return;

            // Two numbers in, one out.
            case "add":
                Binary(stack, ref count, static (a, b) => a + b);
                return;
            case "sub":
                Binary(stack, ref count, static (a, b) => a - b);
                return;
            case "mul":
                Binary(stack, ref count, static (a, b) => a * b);
                return;
            case "div":
                Binary(stack, ref count, static (a, b) => b == 0 ? 0 : a / b);
                return;
            case "idiv":
                Binary(stack, ref count, static (a, b) => (int)b == 0 ? 0 : (int)a / (int)b);
                return;
            case "mod":
                Binary(stack, ref count, static (a, b) => (int)b == 0 ? 0 : (int)a % (int)b);
                return;
            case "exp":
                Binary(stack, ref count, Math.Pow);
                return;
            case "log":
                Unary(stack, count, static x => x > 0 ? Math.Log10(x) : 0);
                return;
            case "atan":
                // The angle is measured anticlockwise from the positive x axis, and reported in the
                // range 0 to 360 rather than the -180 to 180 that the library function gives.
                Binary(stack, ref count, static (numerator, denominator) =>
                {
                    double degrees = double.RadiansToDegrees(Math.Atan2(numerator, denominator));
                    return degrees < 0 ? degrees + 360 : degrees;
                });
                return;

            // Comparison and logic.
            case "eq":
                Binary(stack, ref count, static (a, b) => a == b ? 1 : 0);
                return;
            case "ne":
                Binary(stack, ref count, static (a, b) => a != b ? 1 : 0);
                return;
            case "gt":
                Binary(stack, ref count, static (a, b) => a > b ? 1 : 0);
                return;
            case "ge":
                Binary(stack, ref count, static (a, b) => a >= b ? 1 : 0);
                return;
            case "lt":
                Binary(stack, ref count, static (a, b) => a < b ? 1 : 0);
                return;
            case "le":
                Binary(stack, ref count, static (a, b) => a <= b ? 1 : 0);
                return;
            case "and":
                Binary(stack, ref count, static (a, b) => (int)a & (int)b);
                return;
            case "or":
                Binary(stack, ref count, static (a, b) => (int)a | (int)b);
                return;
            case "xor":
                Binary(stack, ref count, static (a, b) => (int)a ^ (int)b);
                return;
            case "bitshift":
                Binary(stack, ref count, static (value, shift) =>
                    shift >= 0 ? (int)value << (int)shift : (int)value >> (int)-shift);
                return;

            // Stack handling.
            case "pop":
                if (count > 0)
                    count--;

                return;
            case "dup":
                if (count > 0)
                    Push(stack, ref count, stack[count - 1]);

                return;
            case "exch":
                if (count >= 2)
                    (stack[count - 1], stack[count - 2]) = (stack[count - 2], stack[count - 1]);

                return;
            case "copy":
                Copy(stack, ref count);
                return;
            case "index":
                Index(stack, count);
                return;
            case "roll":
                Roll(stack, ref count);
                return;

            default:
                return;
        }
    }

    /// <summary>Duplicates the top n values, in order.</summary>
    private static void Copy(Span<double> stack, ref int count)
    {
        if (count < 1)
            return;

        int n = (int)stack[--count];
        if (n <= 0 || n > count || count + n > stack.Length)
            return;

        stack.Slice(count - n, n).CopyTo(stack[count..]);
        count += n;
    }

    /// <summary>Replaces the top value with the one that many places below it.</summary>
    private static void Index(Span<double> stack, int count)
    {
        if (count < 1)
            return;

        int n = (int)stack[count - 1];
        stack[count - 1] = n >= 0 && n < count - 1 ? stack[count - 2 - n] : 0;
    }

    /// <summary>Rotates the top n values by j places, which is how the language reorders a stack.</summary>
    private static void Roll(Span<double> stack, ref int count)
    {
        if (count < 2)
            return;

        int j = (int)stack[--count];
        int n = (int)stack[--count];

        if (n <= 0 || n > count)
            return;

        Span<double> window = stack.Slice(count - n, n);
        int shift = ((j % n) + n) % n;
        if (shift == 0)
            return;

        // Rotating right by j is three reversals, which needs no scratch space.
        window.Reverse();
        window[..shift].Reverse();
        window[shift..].Reverse();
    }

    private static void Push(Span<double> stack, ref int count, double value)
    {
        if (count < stack.Length)
            stack[count++] = value;
    }

    private static void Unary(Span<double> stack, int count, Func<double, double> operation)
    {
        if (count > 0)
            stack[count - 1] = operation(stack[count - 1]);
    }

    private static void Binary(Span<double> stack, ref int count, Func<double, double, double> operation)
    {
        if (count < 2)
            return;

        double right = stack[--count];
        stack[count - 1] = operation(stack[count - 1], right);
    }
}
