using Inkwright.Markdown;

namespace Inkwright;

public sealed partial class PdfDocument
{
    /// <summary>
    /// Renders the document as Markdown and returns every sidecar image its links use. Nothing is
    /// written to disk and the source PDF is not changed.
    /// </summary>
    public MarkdownDocument ToMarkdown(MarkdownExportOptions? options = null) =>
        MarkdownExporter.Export(this, options ?? MarkdownExportOptions.Default);

    /// <summary>
    /// Renders the document and writes <see cref="MarkdownDocument.DefaultFileName"/> plus any
    /// referenced images into <paramref name="directoryPath"/>.
    /// </summary>
    public async ValueTask ExportMarkdownAsync(
        string directoryPath,
        MarkdownExportOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        MarkdownDocument markdown = ToMarkdown(options);
        cancellationToken.ThrowIfCancellationRequested();
        await markdown.SaveAsync(directoryPath, cancellationToken).ConfigureAwait(false);
    }
}
