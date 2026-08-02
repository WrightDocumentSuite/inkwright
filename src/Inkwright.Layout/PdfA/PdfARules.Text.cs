using Inkwright.Content;
using Inkwright.Cos;

namespace Inkwright.Layout;

internal static partial class PdfARules
{
    private static readonly string[] AdobeCollections = ["Adobe-GB1", "Adobe-CNS1", "Adobe-Japan1", "Adobe-Korea1"];

    /// <summary>
    /// Clause 6.3.8 of part 1, 6.2.11.7 of parts 2 and 3: at levels A and U every glyph shown has to
    /// map back to a character. Without it the pages can be looked at but not read by a machine, and
    /// in fifty years that is most of what reading will be.
    /// </summary>
    private static void CheckToUnicode(PdfAContext context, PdfDictionary font, string name, string subtype)
    {
        if (!context.Conformance.RequiresUnicode)
            return;

        string clause = context.Clause("6.3.8", "6.2.11.7.2");

        if (font.Get(PdfName.Get("ToUnicode")).AsStream is { } map)
        {
            CheckUnicodeValues(context, map, name, clause);
            return;
        }

        if (IsExemptFromToUnicode(context, font, subtype))
            return;

        context.Report(
            clause,
            $"The font '{name}' has no ToUnicode map, so its text cannot be read back. " +
            $"{context.Conformance.Name} requires one.");
    }

    private static bool IsExemptFromToUnicode(PdfAContext context, PdfDictionary font, string subtype)
    {
        string? encoding = font.GetName(PdfNames.Encoding)?.Value;

        if (encoding is "WinAnsiEncoding" or "MacRomanEncoding" or "MacExpertEncoding")
            return true;

        // Part 1 lost this exemption in Corrigendum 2: an Identity CMap says nothing about meaning.
        if (context.Part > 1 && subtype == "TrueType" && (Descriptor(font).GetInteger(PdfNames.Flags, 0) & 4) == 0)
            return true;

        if (subtype != "Type0")
            return false;

        string? ordering = font.GetArray(PdfNames.DescendantFonts)?
            .GetDictionary(0)?
            .GetDictionary(PdfNames.CIDSystemInfo)?
            .GetTextString(PdfNames.Ordering);

        return ordering is not null && AdobeCollections.Any(
            collection => collection.EndsWith(ordering, StringComparison.Ordinal));
    }

    /// <summary>The map has to say something: zero and the byte-order marks are not characters.</summary>
    private static void CheckUnicodeValues(PdfAContext context, PdfStream map, string name, string clause)
    {
        string text;

        try
        {
            text = System.Text.Encoding.Latin1.GetString(map.DecodedSpan());
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            return;
        }

        // Only the destination of a mapping matters. The same digits appear as source codes and in
        // the code space range, where they mean something else entirely.
        foreach (string destination in Destinations(text))
        {
            if (destination is not ("0000" or "FEFF" or "FFFE"))
                continue;

            context.Report(
                clause,
                $"The ToUnicode map of '{name}' maps a glyph to U+{destination}, which is not a character.");

            return;
        }
    }

    private static IEnumerable<string> Destinations(string cmap)
    {
        foreach (string line in cmap.Split('\n'))
        {
            string trimmed = line.Trim();
            bool character = trimmed.EndsWith('>') && trimmed.StartsWith('<');

            if (!character && !trimmed.EndsWith(']'))
                continue;

            List<string> values = [.. Hex(trimmed)];

            // A bfchar line is source then destination; a bfrange line is low, high, then one or
            // more destinations.
            int skip = values.Count >= 3 ? 2 : 1;
            for (int i = skip; i < values.Count; i++)
                yield return values[i].ToUpperInvariant();
        }
    }

    private static IEnumerable<string> Hex(string line)
    {
        int position = 0;

        while (true)
        {
            int open = line.IndexOf('<', position);
            if (open < 0)
                yield break;

            int close = line.IndexOf('>', open);
            if (close < 0)
                yield break;

            yield return line[(open + 1)..close];
            position = close + 1;
        }
    }

    /// <summary>
    /// Clauses 6.2.11.8 (parts 2 and 3) and 6.2.10.9 (part 4): nothing may show the .notdef glyph.
    /// With Identity encoding the glyph index is the character code, so a zero code draws the box.
    /// </summary>
    private static void CheckNotdef(PdfAContext context)
    {
        if (context.Part == 1)
            return;

        foreach (PdfAContentStream stream in PdfAContent.Enumerate(context))
        {
            if (!ShowsNotdef(context, stream))
                continue;

            context.Report(
                context.Part == 4 ? "6.2.10.9" : "6.2.11.8",
                $"The content of {stream.Description} shows the .notdef glyph, which stands for a " +
                "character the font does not have and carries no meaning of its own.");

            return;
        }
    }

    private static bool ShowsNotdef(PdfAContext context, PdfAContentStream stream)
    {
        bool identity = false;

        for (int i = 0; i < stream.Operations.Count; i++)
        {
            PdfOperator op = stream.Operations.GetOperator(i);
            ReadOnlySpan<PdfValue> operands = stream.Operations.GetOperands(i);

            if (op == PdfOperator.SetFont && operands.Length > 0 && operands[0].AsName is { } font)
            {
                identity = UsesIdentityEncoding(context, stream, font);
                continue;
            }

            if (!identity || op is not (PdfOperator.ShowText or PdfOperator.ShowTextAdjusted))
                continue;

            foreach (PdfValue operand in operands)
            {
                if (HasZeroCode(operand))
                    return true;
            }
        }

        return false;
    }

    private static bool HasZeroCode(PdfValue operand)
    {
        if (operand.AsArray is { } array)
        {
            for (int i = 0; i < array.Count; i++)
            {
                if (HasZeroCode(array[i]))
                    return true;
            }

            return false;
        }

        if (operand.Kind != PdfValueKind.String)
            return false;

        ReadOnlySpan<byte> bytes = operand.AsStringBytes;
        for (int i = 0; i + 1 < bytes.Length; i += 2)
        {
            if (bytes[i] == 0 && bytes[i + 1] == 0)
                return true;
        }

        return false;
    }

    private static bool UsesIdentityEncoding(PdfAContext context, PdfAContentStream stream, PdfName name)
    {
        PdfDictionary? font = stream.Resources.GetDictionary(PdfName.Get("Font"))?.GetDictionary(name);
        _ = context;

        return font?.GetName(PdfNames.Encoding)?.Value is "Identity-H" or "Identity-V";
    }

    private static PdfDictionary Descriptor(PdfDictionary font)
    {
        if (font.GetDictionary(PdfNames.FontDescriptor) is { } direct)
            return direct;

        PdfDictionary? descendant = font.GetArray(PdfNames.DescendantFonts)?.GetDictionary(0);
        return descendant?.GetDictionary(PdfNames.FontDescriptor) ?? new PdfDictionary(0);
    }
}
