using System.Text;
using Inkwright.Text;

namespace Inkwright.Markdown;

internal static class MarkdownExporter
{
    public static MarkdownDocument Export(PdfDocument document, MarkdownExportOptions options)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(options);
        ValidateLimits(options);

        if (document.Pages.Count > options.MaximumPages)
        {
            throw new InvalidOperationException(
                $"The PDF has {document.Pages.Count} pages, exceeding MarkdownExportOptions.MaximumPages " +
                $"({options.MaximumPages}).");
        }

        string mediaDirectoryName = MarkdownPath.NormalizeMediaDirectoryName(options.MediaDirectoryName);
        var diagnostics = new MarkdownExportDiagnostics();
        IReadOnlyList<Tagging.PdfStructureNode> structure = document.Structure;
        bool tagged = structure.Count > 0;
        int structureElements = 0;
        var markedContent = new HashSet<(int Page, int Stream, int Mcid)>();
        foreach (Tagging.PdfStructureNode root in structure)
        {
            foreach (Tagging.PdfStructureNode node in root.Descendants())
            {
                if (++structureElements > options.MaximumStructureElements)
                {
                    throw new InvalidOperationException(
                        "The PDF structure tree exceeds MarkdownExportOptions.MaximumStructureElements " +
                        $"({options.MaximumStructureElements}).");
                }

                foreach (Tagging.PdfContentReference reference in node.Content)
                    markedContent.Add((reference.Page.Number, reference.Stream.Number, reference.MarkedContentId));
            }
        }

        if (!tagged && options.StructureMode == MarkdownStructureMode.RequireTagged)
        {
            throw new InvalidOperationException(
                "MarkdownStructureMode.RequireTagged cannot export a PDF without a structure tree.");
        }

        string text;
        IReadOnlyList<MarkdownImage> images = [];
        int resolvedMarkedContent = 0;
        int failedImages = 0;
        int skippedPages = 0;
        if (tagged && options.StructureMode != MarkdownStructureMode.PlainText)
        {
            var renderer = new TaggedMarkdownRenderer(document, options, diagnostics);
            text = renderer.Render();
            images = renderer.Images;
            resolvedMarkedContent = renderer.ResolvedMarkedContentReferences;
            failedImages = renderer.FailedImageResources;
            skippedPages = renderer.SkippedPages;
        }
        else
        {
            text = RenderPlainText(document, useLogicalOrder: false, options, diagnostics, out skippedPages);
            diagnostics.Add(
                MarkdownExportWarningKind.UntaggedFallback,
                tagged
                    ? "Tagged structure was deliberately ignored and page text was exported."
                    : "The PDF has no structure tree; page text was exported without inferred semantics.",
                tagged ? "PlainText" : "Untagged PDF");
        }

        if (text.Length > options.MaximumOutputCharacters)
        {
            throw new InvalidOperationException(
                "The generated Markdown exceeds MarkdownExportOptions.MaximumOutputCharacters " +
                $"({options.MaximumOutputCharacters}).");
        }

        var metrics = new MarkdownExportMetrics(
            document.Pages.Count,
            skippedPages,
            structureElements,
            markedContent.Count,
            resolvedMarkedContent,
            images.Count,
            failedImages,
            diagnostics.Count,
            text.Length);
        return new MarkdownDocument(text, images, mediaDirectoryName, diagnostics, metrics);
    }

    private static string RenderPlainText(
        PdfDocument document,
        bool useLogicalOrder,
        MarkdownExportOptions options,
        MarkdownExportDiagnostics diagnostics,
        out int skippedPages)
    {
        skippedPages = 0;
        var builder = new StringBuilder();
        var extraction = new TextExtractionOptions
        {
            ResolveBidirectional = true,
            SkipHiddenLayers = !options.IncludeHiddenLayers,
            UseLogicalOrder = useLogicalOrder,
            UseActualText = true,
        };

        for (int pageIndex = 0; pageIndex < document.Pages.Count; pageIndex++)
        {
            string pageText;
            try
            {
                pageText = document.Pages[pageIndex].ExtractText(extraction).ReplaceLineEndings("\n");
            }
            catch (Exception exception) when (IsRecoverableContentException(exception))
            {
                skippedPages++;
                diagnostics.Add(
                    MarkdownExportWarningKind.ContentSkipped,
                    "A page content stream could not be decoded or parsed and was skipped.",
                    $"Page {pageIndex + 1}");
                continue;
            }

            foreach (string line in pageText.Split('\n'))
            {
                string trimmed = line.Trim();
                if (trimmed.Length == 0)
                    continue;

                if (builder.Length > 0)
                    builder.Append("\n\n");
                builder.Append(MarkdownText.Escape(trimmed));
            }
        }

        if (builder.Length == 0)
        {
            diagnostics.Add(
                MarkdownExportWarningKind.ContentSkipped,
                "No extractable text was found in the PDF.",
                "Text");
            return "\n";
        }

        builder.Append('\n');
        return builder.ToString();
    }

    internal static bool IsRecoverableContentException(Exception exception) => exception is
        InvalidDataException or InvalidOperationException or NotSupportedException or
        ArgumentException or OverflowException;

    private static void ValidateLimits(MarkdownExportOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumPages);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumStructureElements);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumOutputCharacters);
        ArgumentOutOfRangeException.ThrowIfNegative(options.MaximumImages);
        ArgumentOutOfRangeException.ThrowIfNegative(options.MaximumTotalImageBytes);
    }
}
