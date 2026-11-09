using Inkwright.Cos;
using Inkwright.Diagnostics;

namespace Inkwright.Signing;

/// <summary>
/// Compares two object graphs by what they say rather than by how they are stored, so that an
/// update which only rewrites an object with the same content is not reported as a change.
/// </summary>
internal static class PdfGraphComparer
{
    /// <summary>Bounds the work a crafted file can demand from a verification.</summary>
    private const int MaxComparisons = 2_000_000;

    private static readonly PdfName Parent = PdfName.Get("Parent");
    private static readonly PdfName Length = PdfName.Get("Length");
    private static readonly PdfName Filter = PdfName.Get("Filter");
    private static readonly PdfName DecodeParms = PdfName.Get("DecodeParms");

    internal enum Outcome
    {
        Equal,
        Different,
        TooLargeToCompare,
    }

    /// <summary>Walks both graphs in step. The walk is iterative because the depth is the file's to choose.</summary>
    internal static Outcome Compare(PdfValue left, PdfValue right)
    {
        var visited = new HashSet<(object, object)>();
        var pending = new Stack<(PdfValue Left, PdfValue Right)>();
        pending.Push((left, right));
        int comparisons = 0;

        while (pending.TryPop(out (PdfValue Left, PdfValue Right) pair))
        {
            if (++comparisons > MaxComparisons)
                return Outcome.TooLargeToCompare;

            if (!CompareOne(pair.Left, pair.Right, visited, pending))
                return Outcome.Different;
        }

        return Outcome.Equal;
    }

    private static bool CompareOne(
        PdfValue left,
        PdfValue right,
        HashSet<(object, object)> visited,
        Stack<(PdfValue, PdfValue)> pending)
    {
        if (left.IsNumber && right.IsNumber)
            return Math.Abs(left.AsNumber - right.AsNumber) < 1e-9;

        if (left.Kind != right.Kind)
            return false;

        switch (left.Kind)
        {
            case PdfValueKind.Null:
                return true;
            case PdfValueKind.Boolean:
                return left.AsBoolean == right.AsBoolean;
            case PdfValueKind.Name:
                return left.AsName == right.AsName;
            case PdfValueKind.String:
                return left.AsStringBytes.SequenceEqual(right.AsStringBytes);
            case PdfValueKind.Array:
                return CompareArrays(left.AsArray!, right.AsArray!, visited, pending);
            case PdfValueKind.Dictionary:
                return CompareDictionaries(left.AsDictionary!, right.AsDictionary!, isStream: false, visited, pending);
            case PdfValueKind.Stream:
                return CompareStreams(left.AsStream!, right.AsStream!, visited, pending);
            default:
                return left.Equals(right);
        }
    }

    private static bool CompareArrays(
        PdfArray left,
        PdfArray right,
        HashSet<(object, object)> visited,
        Stack<(PdfValue, PdfValue)> pending)
    {
        if (left.Count != right.Count)
            return false;

        if (visited.Add((left, right)))
        {
            for (int i = 0; i < left.Count; i++)
                pending.Push((left.Get(i), right.Get(i)));
        }

        return true;
    }

    private static bool CompareDictionaries(
        PdfDictionary left,
        PdfDictionary right,
        bool isStream,
        HashSet<(object, object)> visited,
        Stack<(PdfValue, PdfValue)> pending)
    {
        if (!visited.Add((left, right)))
            return true;

        foreach (KeyValuePair<PdfName, PdfValue> entry in left)
        {
            if (!IsIgnored(entry.Key, isStream))
                pending.Push((left.Get(entry.Key), right.Get(entry.Key)));
        }

        foreach (KeyValuePair<PdfName, PdfValue> entry in right)
        {
            if (!left.ContainsKey(entry.Key) && !IsIgnored(entry.Key, isStream))
                pending.Push((PdfValue.Null, right.Get(entry.Key)));
        }

        return true;
    }

    /// <summary>
    /// <c>Parent</c> points back up the page tree, not at anything drawn. How a stream's payload is
    /// encoded says nothing about what it contains, and the payloads are compared decoded.
    /// </summary>
    private static bool IsIgnored(PdfName key, bool isStream) =>
        key == Parent || (isStream && (key == Length || key == Filter || key == DecodeParms));

    private static bool CompareStreams(
        PdfStream left,
        PdfStream right,
        HashSet<(object, object)> visited,
        Stack<(PdfValue, PdfValue)> pending)
    {
        if (!visited.Add((left, right)))
            return true;

        return PayloadsMatch(left, right) &&
               CompareDictionaries(left.Dictionary, right.Dictionary, isStream: true, visited, pending);
    }

    private static bool PayloadsMatch(PdfStream left, PdfStream right)
    {
        if (left.RawData.Span.SequenceEqual(right.RawData.Span))
            return true;

        try
        {
            return left.Decode().Span.SequenceEqual(right.Decode().Span);
        }
        catch (Exception exception) when (exception is PdfFormatException or PdfResourceLimitException or NotSupportedException or InvalidDataException)
        {
            // Two payloads that differ and cannot be decoded cannot be shown to be the same.
            return false;
        }
    }
}
