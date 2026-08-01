using Inkwright.Cos;

namespace Inkwright.Layout;

internal static partial class PdfARules
{
    /// <summary>Annex B of parts 2 and 3: what a digital signature in an archival file may look like.</summary>
    internal static void CheckSignatures(PdfAContext context)
    {
        if (context.Part == 1)
            return;

        foreach (PdfDictionary dictionary in context.Dictionaries)
        {
            if (dictionary.GetName(PdfNames.Type)?.Value == "Sig" || dictionary.ContainsKey(PdfNames.ByteRange))
                CheckSignature(context, dictionary);
        }
    }

    /// <summary>
    /// The digest has to cover the whole file save for the signature itself. A byte range that stops
    /// short leaves room for a page nobody signed, which is the classic way a signed PDF lies.
    /// </summary>
    private static void CheckSignature(PdfAContext context, PdfDictionary signature)
    {
        if (signature.GetName(PdfName.Get("SubFilter"))?.Value is { } subFilter &&
            subFilter is not ("adbe.pkcs7.detached" or "adbe.pkcs7.sha1" or "ETSI.CAdES.detached" or "ETSI.RFC3161"))
        {
            context.Report("B.1", $"The signature uses the SubFilter {subFilter}, which a conforming reader need not know.");
        }

        if (signature.GetArray(PdfNames.ByteRange) is not { Count: 4 } range)
        {
            context.Report("B.1", "The signature has no four-element ByteRange, so what it covers is undefined.");
            return;
        }

        if (!context.HasFile)
            return;

        long start = (long)range.GetNumber(0);
        long firstLength = (long)range.GetNumber(1);
        long secondStart = (long)range.GetNumber(2);
        long secondLength = (long)range.GetNumber(3);
        long total = context.FileBytes.Length;

        if (start != 0)
            context.Report("B.1", "The signature's byte range does not start at the beginning of the file.");

        if (secondStart + secondLength != total)
        {
            context.Report(
                "B.1",
                $"The signature covers up to byte {secondStart + secondLength} of {total}; " +
                "everything after that is unsigned.");
        }

        long gap = secondStart - (start + firstLength);
        if (gap <= 0)
            context.Report("B.1", "The two halves of the byte range do not leave room for the signature itself.");
    }
}
