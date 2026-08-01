using Inkwright.Cos;
using Inkwright.Fonts;

namespace Inkwright.Layout;

internal static partial class PdfARules
{
    private static readonly string[] PredefinedCMaps =
    [
        "Identity-H", "Identity-V",
        "GB-EUC-H", "GB-EUC-V", "GBpc-EUC-H", "GBpc-EUC-V", "GBK-EUC-H", "GBK-EUC-V", "GBKp-EUC-H", "GBKp-EUC-V",
        "GBK2K-H", "GBK2K-V", "UniGB-UCS2-H", "UniGB-UCS2-V", "UniGB-UTF16-H", "UniGB-UTF16-V",
        "B5pc-H", "B5pc-V", "HKscs-B5-H", "HKscs-B5-V", "ETen-B5-H", "ETen-B5-V", "ETenms-B5-H", "ETenms-B5-V",
        "CNS-EUC-H", "CNS-EUC-V", "UniCNS-UCS2-H", "UniCNS-UCS2-V", "UniCNS-UTF16-H", "UniCNS-UTF16-V",
        "83pv-RKSJ-H", "90ms-RKSJ-H", "90ms-RKSJ-V", "90msp-RKSJ-H", "90msp-RKSJ-V", "90pv-RKSJ-H",
        "Add-RKSJ-H", "Add-RKSJ-V", "EUC-H", "EUC-V", "Ext-RKSJ-H", "Ext-RKSJ-V", "H", "V",
        "UniJIS-UCS2-H", "UniJIS-UCS2-V", "UniJIS-UCS2-HW-H", "UniJIS-UCS2-HW-V", "UniJIS-UTF16-H", "UniJIS-UTF16-V",
        "KSC-EUC-H", "KSC-EUC-V", "KSCms-UHC-H", "KSCms-UHC-V", "KSCms-UHC-HW-H", "KSCms-UHC-HW-V",
        "KSCpc-EUC-H", "UniKS-UCS2-H", "UniKS-UCS2-V", "UniKS-UTF16-H", "UniKS-UTF16-V",
    ];

    /// <summary>Clause 6.3 of part 1, clause 6.2.11 of parts 2 and 3: fonts.</summary>
    internal static void CheckFonts(PdfAContext context)
    {
        foreach (PdfDictionary dictionary in context.Dictionaries)
        {
            if (dictionary.GetName(PdfNames.Type)?.Value != "Font")
                continue;

            CheckFont(context, dictionary);
        }

        CheckNotdef(context);
    }

    private static void CheckFont(PdfAContext context, PdfDictionary font)
    {
        string name = font.GetName(PdfName.Get("BaseFont"))?.Value ?? "an unnamed font";
        string subtype = font.GetName(PdfNames.Subtype)?.Value ?? string.Empty;

        if (!IsKnownFontType(subtype))
        {
            context.Report(context.Clause("6.3.2", "6.2.11.2"), $"The font '{name}' has the unknown subtype {subtype}.");
            return;
        }

        // A descendant CIDFont is described by its parent; checking it separately would report the
        // same font twice and blame the half that does not carry the encoding.
        if (subtype is "CIDFontType0" or "CIDFontType2")
            return;

        if (subtype != "Type3")
            CheckEmbedded(context, font, name);

        CheckComposite(context, font, name, subtype);
        CheckTrueTypeEncoding(context, font, name, subtype);
        CheckWidths(context, font, name, subtype);
        CheckToUnicode(context, font, name, subtype);
    }

    private static bool IsKnownFontType(string subtype) => subtype is
        "Type0" or "Type1" or "MMType1" or "TrueType" or "Type3" or "CIDFontType0" or "CIDFontType2";

    private static void CheckEmbedded(PdfAContext context, PdfDictionary font, string name)
    {
        PdfDictionary descriptor = Descriptor(font);
        bool embedded = descriptor.ContainsKey(PdfNames.FontFile) ||
                        descriptor.ContainsKey(PdfNames.FontFile2) ||
                        descriptor.ContainsKey(PdfNames.FontFile3);

        if (!embedded)
        {
            context.Report(
                context.Clause("6.3.4", "6.2.11.4.1"),
                $"The font '{name}' is not embedded. PDF/A has no built-in fonts, so even Helvetica has " +
                "to travel with the file.");

            return;
        }

        CheckSubsetDeclaration(context, font, descriptor, name);
    }

    /// <summary>
    /// Clause 6.3.5 of part 1 makes CharSet and CIDSet mandatory for subsets; parts 2 and 3 only
    /// insist that the declaration be true once it is made. Either way a wrong one is worse than
    /// none, because it claims glyphs the program does not define.
    /// </summary>
    private static void CheckSubsetDeclaration(
        PdfAContext context, PdfDictionary font, PdfDictionary descriptor, string name)
    {
        bool subset = name.Length > 7 && name[6] == '+';
        bool composite = font.GetName(PdfNames.Subtype)?.Value == "Type0";

        if (context.Part != 1 || !subset)
            return;

        if (composite && !descriptor.ContainsKey(PdfNames.CIDSet))
            context.Report("6.3.5", $"The subset font '{name}' has no CIDSet stream, which part 1 requires.");

        if (!composite && descriptor.ContainsKey(PdfNames.FontFile) && !descriptor.ContainsKey(PdfName.Get("CharSet")))
            context.Report("6.3.5", $"The Type 1 subset '{name}' has no CharSet string, which part 1 requires.");
    }

    /// <summary>Clause 6.3.3 of part 1, 6.2.11.3 of parts 2 and 3: composite fonts and their CMaps.</summary>
    private static void CheckComposite(PdfAContext context, PdfDictionary font, string name, string subtype)
    {
        if (subtype != "Type0")
            return;

        PdfDictionary? descendant = font.GetArray(PdfNames.DescendantFonts)?.GetDictionary(0);
        string encoding = font.GetName(PdfNames.Encoding)?.Value ?? string.Empty;

        if (descendant?.GetName(PdfNames.Subtype)?.Value == "CIDFontType2" &&
            !descendant.ContainsKey(PdfNames.CIDToGIDMap))
        {
            context.Report(
                context.Clause("6.3.3.2", "6.2.11.3.2"),
                $"The CIDFont of '{name}' has no CIDToGIDMap entry.");
        }

        if (font.Get(PdfNames.Encoding).AsStream is { } embedded)
        {
            CheckEmbeddedCMap(context, embedded, name);
            return;
        }

        if (encoding.Length > 0 && !PredefinedCMaps.Contains(encoding, StringComparer.Ordinal))
        {
            context.Report(
                context.Clause("6.3.3.3", "6.2.11.3.3"),
                $"The font '{name}' names the CMap {encoding}, which is neither predefined nor embedded.");
        }
    }

    private static void CheckEmbeddedCMap(PdfAContext context, PdfStream cmap, string name)
    {
        string clause = context.Clause("6.3.3.3", "6.2.11.3.3");
        long declared = cmap.Dictionary.GetInteger(PdfName.Get("WMode"), 0);

        string text;
        try
        {
            text = System.Text.Encoding.Latin1.GetString(cmap.DecodedSpan());
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            return;
        }

        int marker = text.IndexOf("/WMode", StringComparison.Ordinal);
        if (marker >= 0)
        {
            string tail = text[(marker + 6)..].TrimStart();
            if (tail.Length > 0 && char.IsDigit(tail[0]) && tail[0] - '0' != declared)
                context.Report(clause, $"The CMap of '{name}' declares WMode {declared} but its stream says {tail[0]}.");
        }

        if (!text.Contains("usecmap", StringComparison.Ordinal))
            return;

        foreach (string predefined in PredefinedCMaps)
        {
            if (text.Contains(predefined, StringComparison.Ordinal))
                return;
        }

        context.Report(clause, $"The CMap of '{name}' builds on another CMap that is not one of the predefined ones.");
    }

    /// <summary>Clause 6.3.7 of part 1, 6.2.11.6 of parts 2 and 3: how codes reach glyphs.</summary>
    private static void CheckTrueTypeEncoding(PdfAContext context, PdfDictionary font, string name, string subtype)
    {
        if (subtype != "TrueType")
            return;

        string clause = context.Clause("6.3.7", "6.2.11.6");
        long flags = Descriptor(font).GetInteger(PdfNames.Flags, 0);
        bool symbolic = (flags & 4) != 0 && (flags & 32) == 0;

        PdfValue encoding = font.Get(PdfNames.Encoding);

        if (symbolic)
        {
            if (font.ContainsKey(PdfNames.Encoding))
                context.Report(clause, $"The symbolic TrueType font '{name}' carries an Encoding entry, which PDF/A forbids.");

            return;
        }

        string? baseEncoding = encoding.AsName?.Value ?? encoding.AsDictionary?.GetName(PdfName.Get("BaseEncoding"))?.Value;
        if (baseEncoding is not ("WinAnsiEncoding" or "MacRomanEncoding"))
        {
            context.Report(
                clause,
                $"The non-symbolic TrueType font '{name}' names {baseEncoding ?? "no"} encoding; PDF/A wants " +
                "WinAnsiEncoding or MacRomanEncoding.");
        }

        if (context.Part == 1 && encoding.AsDictionary?.ContainsKey(PdfName.Get("Differences")) == true)
            context.Report(clause, $"The non-symbolic TrueType font '{name}' defines a Differences array, which part 1 forbids.");
    }

    /// <summary>
    /// Clause 6.3.6 of part 1, 6.2.11.5 of parts 2 and 3: the widths in the dictionary and the
    /// widths in the program have to agree, or the text moves depending on which one a reader reads.
    /// </summary>
    private static void CheckWidths(PdfAContext context, PdfDictionary font, string name, string subtype)
    {
        if (subtype != "Type0")
            return;

        PdfDictionary? descendant = font.GetArray(PdfNames.DescendantFonts)?.GetDictionary(0);
        if (descendant is null || LoadProgram(descendant) is not { } program)
            return;

        string clause = context.Clause("6.3.6", "6.2.11.5");

        foreach ((int cid, double declared) in CidWidths(descendant))
        {
            if (cid >= program.GlyphCount)
                continue;

            double actual = program.GetAdvanceWidthPerMille((ushort)cid);
            if (Math.Abs(actual - declared) <= 1)
                continue;

            context.Report(
                clause,
                $"The font '{name}' declares width {declared:0.#} for CID {cid} but its program says {actual:0.#}.");

            return;
        }
    }

    private static TrueTypeFont? LoadProgram(PdfDictionary descendant)
    {
        PdfDictionary descriptor = descendant.GetDictionary(PdfNames.FontDescriptor) ?? new PdfDictionary(0);
        PdfStream? file = descriptor.GetStream(PdfNames.FontFile2) ?? descriptor.GetStream(PdfNames.FontFile3);

        if (file is null)
            return null;

        try
        {
            return TrueTypeFont.Parse(file.DecodedSpan().ToArray());
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or IndexOutOfRangeException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The widths a CIDFont declares, read out of the compact W array of Table 116.</summary>
    private static IEnumerable<(int Cid, double Width)> CidWidths(PdfDictionary descendant)
    {
        if (descendant.GetArray(PdfName.Get("W")) is not { } widths)
            yield break;

        int i = 0;
        while (i < widths.Count)
        {
            int first = (int)widths.GetNumber(i);

            if (i + 1 < widths.Count && widths.Get(i + 1).AsArray is { } list)
            {
                for (int k = 0; k < list.Count; k++)
                    yield return (first + k, list.GetNumber(k));

                i += 2;
                continue;
            }

            if (i + 2 >= widths.Count)
                yield break;

            int last = (int)widths.GetNumber(i + 1);
            double width = widths.GetNumber(i + 2);

            for (int cid = first; cid <= last && cid - first < 1024; cid++)
                yield return (cid, width);

            i += 3;
        }
    }
}
