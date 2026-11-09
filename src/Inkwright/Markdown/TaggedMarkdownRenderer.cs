using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Inkwright.Cos;
using Inkwright.Images;
using Inkwright.Tagging;
using Inkwright.Text;

namespace Inkwright.Markdown;

/// <summary>Projects the logical structure tree defined by ISO 32000-2 §§14.7–14.9.</summary>
internal sealed class TaggedMarkdownRenderer
{
    private static readonly HashSet<string> TransparentContainers = new(StringComparer.Ordinal)
    {
        "Document", "DocumentFragment", "Part", "Art", "Sect", "Div", "Aside",
        "NonStruct", "Private", "TOC", "TOCI", "Index",
    };

    private readonly PdfDocument _document;
    private readonly MarkdownExportOptions _options;
    private readonly MarkdownExportDiagnostics _diagnostics;
    private readonly Dictionary<int, PdfPage> _pages = [];
    private readonly Dictionary<int, IReadOnlyList<PdfLetter>> _letters = [];
    private readonly Dictionary<(int Page, int Stream, int Mcid), string> _content = [];
    private readonly Dictionary<int, IReadOnlyList<PdfImageOccurrence>> _imageOccurrences = [];
    private readonly Dictionary<(PdfStream Stream, PdfDictionary Resources), MarkdownImage> _imageResources = [];
    private readonly HashSet<(PdfStream Stream, PdfDictionary Resources)> _failedImageResources = [];
    private readonly HashSet<(int Page, int Stream, int Mcid)> _resolvedContent = [];
    private readonly HashSet<int> _skippedPages = [];
    private readonly Dictionary<string, MarkdownImage> _imageHashes = new(StringComparer.Ordinal);
    private readonly List<string> _blocks = [];
    private readonly List<MarkdownImage> _images = [];
    private readonly string _mediaDirectoryName;
    private long _totalImageBytes;

    internal TaggedMarkdownRenderer(
        PdfDocument document,
        MarkdownExportOptions options,
        MarkdownExportDiagnostics diagnostics)
    {
        _document = document;
        _options = options;
        _diagnostics = diagnostics;
        _mediaDirectoryName = MarkdownPath.NormalizeMediaDirectoryName(options.MediaDirectoryName);

        for (int i = 0; i < document.Pages.Count; i++)
            _pages.TryAdd(document.Pages[i].Id.Number, document.Pages[i]);
    }

    internal IReadOnlyList<MarkdownImage> Images => _images;

    internal int ResolvedMarkedContentReferences => _resolvedContent.Count;

    internal int FailedImageResources => _failedImageResources.Count;

    internal int SkippedPages => _skippedPages.Count;

    internal string Render()
    {
        foreach (PdfStructureNode root in _document.Structure)
            RenderBlock(root);

        if (_blocks.Count == 0)
        {
            _diagnostics.Add(
                MarkdownExportWarningKind.ContentSkipped,
                "The structure tree contained no text that could be exported.",
                "Tagged PDF");
            return "\n";
        }

        return string.Join("\n\n", _blocks) + "\n";
    }

    private void RenderBlock(PdfStructureNode node)
    {
        if (node.ActualText is { } codeReplacement &&
            HasStandardPdfSemantics(node) &&
            node.ResolvedTag == "Code")
        {
            string code = ReplacementText(codeReplacement);
            if (code.Length > 0)
                _blocks.Add(MarkdownText.CodeBlock(code));
            return;
        }

        if (node.ActualText is { } replacement)
        {
            string exact = ReplacementText(replacement);
            if (exact.Length > 0)
                _blocks.Add(MarkdownText.Escape(exact));
            return;
        }

        if (!HasStandardPdfSemantics(node))
        {
            ReportNamespaceApproximation(node);
            RenderContainer(node);
            return;
        }

        string tag = node.ResolvedTag;

        if (TransparentContainers.Contains(tag))
        {
            RenderContainer(node);
            return;
        }

        if (HeadingLevel(tag) is { } headingLevel)
        {
            AddTextBlock(node, new string('#', headingLevel) + " ");
            return;
        }

        switch (tag)
        {
            case "H":
                int inferred = InferredHeadingLevel(node);
                _diagnostics.Add(
                    MarkdownExportWarningKind.StructureApproximated,
                    "A generic H structure element was assigned a Markdown heading level from its section depth.",
                    "H");
                AddTextBlock(node, new string('#', inferred) + " ");
                break;

            case "P":
            case "Caption":
            case "Title":
            case "Sub":
                AddTextBlock(node);
                break;

            case "BlockQuote":
                string quote = RenderInline(node);
                if (quote.Length > 0)
                    _blocks.Add("> " + quote.Replace("\n", "\n> ", StringComparison.Ordinal));
                break;

            case "Code":
                string code = NodeText(node);
                if (code.Length > 0)
                    _blocks.Add(MarkdownText.CodeBlock(code));
                break;

            case "L":
                RenderList(node);
                break;

            case "Table":
                RenderTable(node);
                break;

            case "Figure":
                string figure = FigureMarkdown(node);
                if (figure.Length > 0)
                    _blocks.Add(figure);
                break;

            case "Formula":
                string formula = NormalizeText(
                    node.ActualText ?? node.AlternateText ?? SemanticText(node));
                if (formula.Length > 0)
                    _blocks.Add(MarkdownText.Escape(formula));
                else
                    ReportSkipped(tag, "A Formula element had neither text nor an alternate description.");
                break;

            case "Form":
                InlineFragment form = FormFragment(node);
                if (form.Markdown.Length > 0)
                    _blocks.Add(form.Markdown);
                else
                    ReportSkipped(tag, "A Form element had no readable field value or description.");
                break;

            case "Artifact":
                break;

            default:
                _diagnostics.Add(
                    MarkdownExportWarningKind.StructureApproximated,
                    "An unsupported structure type was flattened while preserving its text and child order.",
                    tag);
                RenderContainer(node);
                break;
        }
    }

    private void RenderContainer(PdfStructureNode node)
    {
        var direct = new StringBuilder();
        foreach (PdfStructureKid kid in node.Kids)
        {
            switch (kid)
            {
                case PdfStructureContentKid content:
                    AppendFragment(direct, ContentText(content.Reference));
                    break;

                case PdfStructureElementKid element:
                    FlushDirect(direct);
                    RenderBlock(element.Element);
                    break;

                case PdfStructureObjectKid:
                    ReportSkipped("OBJR", "A structure object reference could not yet be projected as Markdown.");
                    break;
            }
        }

        FlushDirect(direct);
    }

    private void FlushDirect(StringBuilder direct)
    {
        string text = NormalizeText(direct.ToString());
        direct.Clear();
        if (text.Length > 0)
            _blocks.Add(MarkdownText.Escape(text));
    }

    private void AddTextBlock(PdfStructureNode node, string prefix = "")
    {
        string text = RenderInline(node);
        if (text.Length > 0)
            _blocks.Add(prefix + text);
    }

    private string RenderInline(PdfStructureNode node)
        => Inline(node, skipNestedLists: false).Markdown;

    private InlineFragment Inline(PdfStructureNode node, bool skipNestedLists)
    {
        if (node.ActualText is { } actual)
        {
            string replacement = ReplacementText(actual);
            return new InlineFragment(MarkdownText.Escape(replacement), replacement);
        }

        var markdown = new StringBuilder();
        var plain = new StringBuilder();
        foreach (PdfStructureKid kid in node.Kids)
        {
            InlineFragment fragment;
            switch (kid)
            {
                case PdfStructureContentKid content:
                    string text = ContentText(content.Reference);
                    fragment = new InlineFragment(MarkdownText.Escape(text), text);
                    break;

                case PdfStructureElementKid element when
                    skipNestedLists && element.Element.ResolvedTag is "L" or "Table":
                    continue;

                case PdfStructureElementKid element:
                    fragment = InlineElement(element.Element, skipNestedLists);
                    break;

                case PdfStructureObjectKid when node.ResolvedTag == "Link":
                    // The Link renderer consumes its annotation after the visible label has been
                    // assembled. It is not skipped merely because it has no inline text itself.
                    continue;

                case PdfStructureObjectKid:
                    ReportSkipped("OBJR", "A structure object reference could not be projected as inline Markdown.");
                    continue;

                default:
                    continue;
            }

            AppendInline(markdown, plain, fragment);
        }

        return new InlineFragment(markdown.ToString(), plain.ToString());
    }

    private InlineFragment InlineElement(PdfStructureNode node, bool skipNestedLists)
    {
        if (node.ResolvedTag == "Figure" && node.ActualText is { } actual)
        {
            string replacement = ReplacementText(actual);
            return new InlineFragment(MarkdownText.Escape(replacement), replacement);
        }

        if (!HasStandardPdfSemantics(node))
        {
            ReportNamespaceApproximation(node);
            return Inline(node, skipNestedLists);
        }

        if (node.ResolvedTag == "Figure")
        {
            string markdown = FigureMarkdown(node);
            string plain = NormalizeText(node.ActualText ?? node.AlternateText ?? string.Empty);
            return new InlineFragment(markdown, plain);
        }

        if (node.ResolvedTag == "Form")
            return FormFragment(node);

        InlineFragment inner = Inline(node, skipNestedLists);
        if (inner.Markdown.Length == 0)
            return inner;

        return node.ResolvedTag switch
        {
            "Link" => Link(node, inner),
            "Code" => new InlineFragment(MarkdownText.CodeSpan(inner.Plain), inner.Plain),
            "Strong" => new InlineFragment($"**{inner.Markdown}**", inner.Plain),
            "Em" => new InlineFragment($"*{inner.Markdown}*", inner.Plain),
            "Quote" => new InlineFragment($"\"{inner.Markdown}\"", $"\"{inner.Plain}\""),
            _ => inner,
        };
    }

    private InlineFragment Link(PdfStructureNode node, InlineFragment label)
    {
        string? destination = LinkDestination(node);
        if (destination is null)
            return label;

        if (!MarkdownText.IsSafeLinkDestination(destination, out string scheme))
        {
            _diagnostics.Add(
                MarkdownExportWarningKind.UnsafeLinkSkipped,
                "A potentially executable link target was omitted.",
                scheme);
            return label;
        }

        return new InlineFragment(
            $"[{label.Markdown}]({MarkdownText.LinkDestination(destination)})",
            label.Plain);
    }

    private string NodeText(PdfStructureNode node) => Inline(node, skipNestedLists: false).Plain;

    private void RenderList(PdfStructureNode list)
    {
        string numbering = PdfStructureAttributes.InheritedName(list, "List", "ListNumbering") ?? "None";
        bool ordered = numbering is "Ordered" or "Decimal" or "UpperRoman" or "LowerRoman" or
            "UpperAlpha" or "LowerAlpha";
        bool description = numbering == "Description";
        List<PdfStructureNode> items = [.. list.Children.Where(child => child.ResolvedTag == "LI")];
        ReportListContinuation(list);

        if (items.Count == 0)
        {
            _diagnostics.Add(
                MarkdownExportWarningKind.StructureApproximated,
                "A list without LI children was flattened.",
                "L");
            RenderContainer(list);
            return;
        }

        if (description)
        {
            RenderDescriptionList(items);
            return;
        }

        var block = new StringBuilder();
        for (int index = 0; index < items.Count; index++)
        {
            PdfStructureNode item = items[index];
            PdfStructureNode? label = item.Children.FirstOrDefault(child => child.ResolvedTag == "Lbl");
            PdfStructureNode? body = item.Children.FirstOrDefault(child => child.ResolvedTag == "LBody");
            InlineFragment content = Inline(body ?? item, skipNestedLists: true);
            (string marker, string labelPrefix) = ListMarker(
                numbering,
                ordered,
                index + 1,
                label is null ? string.Empty : NodeText(label));

            if (block.Length > 0)
                block.Append('\n');
            block.Append(marker);
            block.Append(labelPrefix);
            block.Append(content.Markdown);

            foreach (PdfStructureNode nested in NestedLists(body ?? item))
            {
                string nestedBlock = BuildNestedList(nested, depth: 1);
                if (nestedBlock.Length > 0)
                {
                    block.Append('\n');
                    block.Append(nestedBlock);
                }
            }
        }

        _blocks.Add(block.ToString());
    }

    private string BuildNestedList(PdfStructureNode list, int depth)
    {
        string numbering = PdfStructureAttributes.InheritedName(list, "List", "ListNumbering") ?? "None";
        bool ordered = numbering is "Ordered" or "Decimal" or "UpperRoman" or "LowerRoman" or
            "UpperAlpha" or "LowerAlpha";
        ReportListContinuation(list);
        var block = new StringBuilder();
        int index = 0;

        foreach (PdfStructureNode item in list.Children.Where(child => child.ResolvedTag == "LI"))
        {
            PdfStructureNode? body = item.Children.FirstOrDefault(child => child.ResolvedTag == "LBody");
            PdfStructureNode? label = item.Children.FirstOrDefault(child => child.ResolvedTag == "Lbl");
            InlineFragment content = Inline(body ?? item, skipNestedLists: true);
            (string marker, string labelPrefix) = ListMarker(
                numbering,
                ordered,
                index + 1,
                label is null ? string.Empty : NodeText(label));
            if (block.Length > 0)
                block.Append('\n');
            block.Append(' ', depth * 4);
            block.Append(marker);
            block.Append(labelPrefix);
            block.Append(content.Markdown);
            index++;

            foreach (PdfStructureNode nested in NestedLists(body ?? item))
            {
                block.Append('\n');
                block.Append(BuildNestedList(nested, depth + 1));
            }
        }

        return block.ToString();
    }

    private (string Marker, string LabelPrefix) ListMarker(
        string numbering,
        bool ordered,
        int index,
        string explicitLabel)
    {
        explicitLabel = NormalizeText(explicitLabel);
        if (numbering is "Ordered" or "Decimal")
        {
            if (TryOrderedListMarker(explicitLabel, out string? marker))
                return (marker + " ", string.Empty);

            if (explicitLabel.Length > 0)
            {
                _diagnostics.Add(
                    MarkdownExportWarningKind.StructureApproximated,
                    "A non-CommonMark explicit label was preserved inside an ordered list item.",
                    "Custom ordered-list label");
                return ("- ", MarkdownText.Escape(explicitLabel) + " ");
            }

            return ($"{index}. ", string.Empty);
        }

        if (ordered)
        {
            _diagnostics.Add(
                MarkdownExportWarningKind.StructureApproximated,
                "A non-decimal PDF list marker was preserved as item text because Markdown only numbers lists in decimal.",
                $"{numbering} list");
            return explicitLabel.Length > 0
                ? ("- ", MarkdownText.Escape(explicitLabel) + " ")
                : ($"{index}. ", string.Empty);
        }

        if (numbering == "None" && explicitLabel.Length > 0)
        {
            _diagnostics.Add(
                MarkdownExportWarningKind.StructureApproximated,
                "An arbitrary PDF list label was preserved inside a Markdown list item.",
                "Custom list label");
            return ("- ", MarkdownText.Escape(explicitLabel) + " ");
        }

        return ("- ", string.Empty);
    }

    private void ReportListContinuation(PdfStructureNode list)
    {
        if (!PdfStructureAttributes.Boolean(list, "List", "ContinuedList") &&
            !PdfStructureAttributes.Has(list, "List", "ContinuedFrom"))
        {
            return;
        }

        _diagnostics.Add(
            MarkdownExportWarningKind.StructureApproximated,
            "A continued PDF list starts a new Markdown list because Markdown has no cross-block list identity.",
            "Continued list");
    }

    private static bool TryOrderedListMarker(string label, out string? marker)
    {
        marker = null;
        if (label.Length is < 2 or > 10 || label[^1] is not ('.' or ')'))
            return false;

        ReadOnlySpan<char> digits = label.AsSpan(0, label.Length - 1);
        if (digits.Length > 9 || !digits.ToString().All(char.IsAsciiDigit))
            return false;

        marker = label;
        return true;
    }

    private void RenderDescriptionList(IReadOnlyList<PdfStructureNode> items)
    {
        var html = new StringBuilder("<dl>");
        foreach (PdfStructureNode item in items)
        {
            PdfStructureNode? label = item.Children.FirstOrDefault(child => child.ResolvedTag == "Lbl");
            PdfStructureNode? body = item.Children.FirstOrDefault(child => child.ResolvedTag == "LBody");
            html.Append("\n  <dt>");
            html.Append(MarkdownText.HtmlText(label is null ? string.Empty : NodeText(label)));
            html.Append("</dt>\n  <dd>");
            html.Append(MarkdownText.HtmlText(body is null ? NodeText(item) : NodeText(body)));
            html.Append("</dd>");
        }
        html.Append("\n</dl>");
        _blocks.Add(html.ToString());
        _diagnostics.Add(
            MarkdownExportWarningKind.HtmlFallbackUsed,
            "A PDF description list was emitted as HTML because Markdown has no description-list syntax.",
            "Description list");
    }

    private void RenderTable(PdfStructureNode table)
    {
        List<PdfStructureNode> rowNodes = [];
        CollectRows(table, rowNodes);
        List<List<TableCell>> rows = [.. rowNodes.Select(RowCells).Where(row => row.Count > 0)];

        if (rows.Count == 0)
        {
            _diagnostics.Add(
                MarkdownExportWarningKind.StructureApproximated,
                "A table without readable rows was flattened.",
                "Table");
            RenderContainer(table);
            return;
        }

        bool hasSpans = rows.SelectMany(row => row).Any(cell => cell.RowSpan > 1 || cell.ColumnSpan > 1);
        bool headerShapeNeedsHtml =
            rows.Skip(1).SelectMany(row => row).Any(cell => cell.Header) ||
            rows[0].Any(cell => cell.Header) && rows[0].Any(cell => !cell.Header);
        if (_options.Flavor == MarkdownFlavor.CommonMark || hasSpans || headerShapeNeedsHtml)
        {
            _blocks.Add(HtmlTable(rows));
            _diagnostics.Add(
                MarkdownExportWarningKind.HtmlFallbackUsed,
                hasSpans
                    ? "A table with row or column spans was emitted as HTML."
                    : headerShapeNeedsHtml
                        ? "A table with row or mixed header cells was emitted as HTML."
                    : "A table was emitted as HTML because CommonMark has no table syntax.",
                "Table");
            return;
        }

        _blocks.Add(GfmTable(rows));
    }

    private List<TableCell> RowCells(PdfStructureNode row)
    {
        List<TableCell> cells = [];
        foreach (PdfStructureNode cell in row.Children.Where(child => child.ResolvedTag is "TH" or "TD"))
        {
            InlineFragment content = Inline(cell, skipNestedLists: true);
            cells.Add(new TableCell(
                content.Markdown,
                content.Plain,
                cell.ResolvedTag == "TH",
                PdfStructureAttributes.Integer(cell, "Table", "RowSpan"),
                PdfStructureAttributes.Integer(cell, "Table", "ColSpan")));
        }
        return cells;
    }

    private static string GfmTable(IReadOnlyList<List<TableCell>> rows)
    {
        int columns = rows.Max(row => row.Count);
        bool firstIsHeader = rows[0].Any(cell => cell.Header);
        var builder = new StringBuilder();

        if (firstIsHeader)
            AppendPipeRow(builder, rows[0], columns);
        else
            AppendPipeRow(builder, [], columns);
        AppendSeparator(builder, columns);

        int firstData = firstIsHeader ? 1 : 0;
        for (int i = firstData; i < rows.Count; i++)
            AppendPipeRow(builder, rows[i], columns);

        return builder.ToString().TrimEnd('\n');
    }

    private static void AppendPipeRow(StringBuilder builder, IReadOnlyList<TableCell> row, int columns)
    {
        builder.Append('|');
        for (int column = 0; column < columns; column++)
        {
            builder.Append(' ');
            if (column < row.Count)
                builder.Append(TableCellMarkdown(row[column].Markdown));
            builder.Append(" |");
        }
        builder.Append('\n');
    }

    private static void AppendSeparator(StringBuilder builder, int columns)
    {
        builder.Append('|');
        for (int column = 0; column < columns; column++)
            builder.Append(" --- |");
        builder.Append('\n');
    }

    private static string HtmlTable(IReadOnlyList<List<TableCell>> rows)
    {
        var html = new StringBuilder("<table>");
        foreach (List<TableCell> row in rows)
        {
            html.Append("\n  <tr>");
            foreach (TableCell cell in row)
            {
                string element = cell.Header ? "th" : "td";
                html.Append("\n    <");
                html.Append(element);
                if (cell.RowSpan > 1)
                    html.Append(CultureInfo.InvariantCulture, $" rowspan=\"{cell.RowSpan}\"");
                if (cell.ColumnSpan > 1)
                    html.Append(CultureInfo.InvariantCulture, $" colspan=\"{cell.ColumnSpan}\"");
                html.Append('>');
                html.Append(MarkdownText.HtmlText(cell.Plain));
                html.Append("</");
                html.Append(element);
                html.Append('>');
            }
            html.Append("\n  </tr>");
        }
        html.Append("\n</table>");
        return html.ToString();
    }

    private static void CollectRows(PdfStructureNode node, List<PdfStructureNode> rows)
    {
        foreach (PdfStructureNode child in node.Children)
        {
            if (child.ResolvedTag == "TR")
                rows.Add(child);
            else if (child.ResolvedTag is "THead" or "TBody" or "TFoot")
                CollectRows(child, rows);
        }
    }

    private static IEnumerable<PdfStructureNode> NestedLists(PdfStructureNode node)
    {
        foreach (PdfStructureNode child in node.Children)
        {
            if (child.ResolvedTag == "L")
                yield return child;
            else
            {
                foreach (PdfStructureNode nested in NestedLists(child))
                    yield return nested;
            }
        }
    }

    private string? LinkDestination(PdfStructureNode node)
    {
        foreach (PdfObjectReference reference in node.Objects)
        {
            if (_document.GetObject(reference.Object).AsDictionary is not { } annotation)
                continue;

            PdfDictionary? action = annotation.GetDictionary(PdfName.Get("A"));
            if (action?.GetName(PdfName.Get("S"))?.Value == "URI" &&
                action.GetTextString(PdfName.Get("URI")) is { Length: > 0 } uri)
            {
                return ResolveDocumentUri(uri);
            }

            if (!annotation.Get(PdfName.Get("Dest")).IsNull ||
                (action?.GetName(PdfName.Get("S"))?.Value == "GoTo" &&
                 (!action.Get(PdfName.Get("D")).IsNull || !action.Get(PdfName.Get("SD")).IsNull)))
            {
                _diagnostics.Add(
                    MarkdownExportWarningKind.StructureApproximated,
                    "An internal PDF destination was left as text because the export has no stable page anchors.",
                    "Internal link");
            }
        }

        return null;
    }

    private string ResolveDocumentUri(string destination)
    {
        if (Uri.TryCreate(destination, UriKind.Absolute, out _))
            return destination;

        string? declaredBase = _document.Catalog
            .GetDictionary(PdfName.Get("URI"))?
            .GetTextString(PdfName.Get("Base"));
        if (declaredBase is null ||
            !Uri.TryCreate(declaredBase, UriKind.Absolute, out Uri? baseUri) ||
            !Uri.TryCreate(baseUri, destination, out Uri? resolved))
        {
            return destination;
        }

        return resolved.AbsoluteUri;
    }

    private string FigureMarkdown(PdfStructureNode figure)
    {
        string alternative = NormalizeText(figure.AlternateText ?? figure.ActualText ?? string.Empty);
        if (!_options.IncludeImages)
            return MarkdownText.Escape(alternative);

        List<PdfPageImage> candidates = FigureImages(figure);
        var rendered = new List<string>(candidates.Count);
        foreach (PdfPageImage candidate in candidates)
        {
            if (TryAddImage(candidate, out MarkdownImage? image) && image is not null)
            {
                string path = MarkdownUrlPath(_mediaDirectoryName, image.FileName);
                rendered.Add($"![{MarkdownText.Escape(alternative)}]({path})");
            }
        }

        if (rendered.Count > 0)
            return string.Join(" ", rendered);

        if (alternative.Length > 0)
        {
            _diagnostics.Add(
                MarkdownExportWarningKind.ImageSkipped,
                "A Figure had no uniquely associated decodable raster image; its alternate text was emitted.",
                "Figure");
            return MarkdownText.Escape(alternative);
        }

        _diagnostics.Add(
            MarkdownExportWarningKind.ImageSkipped,
            "A Figure had no uniquely associated decodable raster image or alternate text.",
            "Figure");
        return string.Empty;
    }

    private List<PdfPageImage> FigureImages(PdfStructureNode figure)
    {
        List<PdfPageImage> images = [];
        var seen = new HashSet<(PdfStream Stream, PdfDictionary Resources)>();

        foreach (PdfObjectReference reference in AllObjectReferences(figure))
        {
            if (_document.GetObject(reference.Object).AsStream is not { } stream ||
                stream.Dictionary.GetName(PdfNames.Subtype) != PdfNames.Image ||
                !_pages.TryGetValue(reference.Page.Number, out PdfPage? page))
            {
                continue;
            }

            if (!_options.IncludeHiddenLayers &&
                stream.Dictionary[PdfNames.OC] is { IsNull: false } group &&
                !_document.OptionalContent.IsVisible(group))
            {
                continue;
            }

            var image = new PdfPageImage($"object-{reference.Object.Number}", stream, page.Resources);
            if (seen.Add((stream, page.Resources)))
                images.Add(image);
        }

        foreach (PdfContentReference content in AllContentReferences(figure))
        {
            if (!_pages.TryGetValue(content.Page.Number, out PdfPage? page))
                continue;

            if (!_imageOccurrences.TryGetValue(content.Page.Number, out IReadOnlyList<PdfImageOccurrence>? occurrences))
            {
                try
                {
                    occurrences = PageImageOccurrenceFinder.Find(page, skipHiddenLayers: !_options.IncludeHiddenLayers);
                }
                catch (Exception exception) when (MarkdownExporter.IsRecoverableContentException(exception))
                {
                    _skippedPages.Add(page.Id.Number);
                    _diagnostics.Add(
                        MarkdownExportWarningKind.ContentSkipped,
                        "A page content stream could not be decoded or parsed while locating images.",
                        $"Page {PageNumber(page)}");
                    occurrences = [];
                }
                _imageOccurrences.Add(content.Page.Number, occurrences);
            }

            foreach (PdfImageOccurrence occurrence in occurrences)
            {
                if (occurrence.MarkedContentId == content.MarkedContentId &&
                    occurrence.MarkedContentStream.Number == content.Stream.Number &&
                    seen.Add((occurrence.Image.Stream, occurrence.Image.Resources)))
                {
                    images.Add(occurrence.Image);
                }
            }
        }

        return images;
    }

    private bool TryAddImage(PdfPageImage source, out MarkdownImage? image)
    {
        var resourceKey = (source.Stream, source.Resources);
        if (_imageResources.TryGetValue(resourceKey, out image))
            return true;
        if (_failedImageResources.Contains(resourceKey))
        {
            image = null;
            return false;
        }

        try
        {
            byte[] png = source.Decode().ToPng();
            string hash = Convert.ToHexString(SHA256.HashData(png));
            if (_imageHashes.TryGetValue(hash, out image))
            {
                _imageResources.Add(resourceKey, image);
                return true;
            }

            if (_images.Count >= _options.MaximumImages ||
                png.LongLength > _options.MaximumTotalImageBytes - _totalImageBytes)
            {
                _failedImageResources.Add(resourceKey);
                _diagnostics.Add(
                    MarkdownExportWarningKind.ImageSkipped,
                    "A raster image was omitted because the configured Markdown sidecar limit was reached.",
                    "Image resource limit");
                image = null;
                return false;
            }

            image = new MarkdownImage
            {
                FileName = $"image{_images.Count + 1}.png",
                Content = png,
                ContentType = "image/png",
            };
            _images.Add(image);
            _totalImageBytes += png.LongLength;
            _imageHashes.Add(hash, image);
            _imageResources.Add(resourceKey, image);
            return true;
        }
        catch (Exception exception) when (exception is
            InvalidDataException or InvalidOperationException or NotSupportedException or
            ArgumentException or OverflowException or Diagnostics.PdfFormatException)
        {
            _failedImageResources.Add(resourceKey);
            _diagnostics.Add(
                MarkdownExportWarningKind.ImageSkipped,
                "A raster image could not be decoded and was omitted.",
                source.Codec ?? "unfiltered");
            image = null;
            return false;
        }
    }

    private static IEnumerable<PdfContentReference> AllContentReferences(PdfStructureNode node)
    {
        foreach (PdfStructureKid kid in node.Kids)
        {
            if (kid is PdfStructureContentKid content)
                yield return content.Reference;
            else if (kid is PdfStructureElementKid element)
            {
                foreach (PdfContentReference nested in AllContentReferences(element.Element))
                    yield return nested;
            }
        }
    }

    private static IEnumerable<PdfObjectReference> AllObjectReferences(PdfStructureNode node)
    {
        foreach (PdfStructureKid kid in node.Kids)
        {
            if (kid is PdfStructureObjectKid content)
                yield return content.Reference;
            else if (kid is PdfStructureElementKid element)
            {
                foreach (PdfObjectReference nested in AllObjectReferences(element.Element))
                    yield return nested;
            }
        }
    }

    private InlineFragment FormFragment(PdfStructureNode form)
    {
        var fields = new List<InlineFragment>();
        foreach (PdfObjectReference reference in AllObjectReferences(form))
        {
            if (_document.GetObject(reference.Object).AsDictionary is not { } dictionary ||
                dictionary.GetName(PdfNames.Subtype) != PdfNames.Widget &&
                InheritedFieldValue(dictionary, PdfName.Get("FT")).AsName is null)
            {
                continue;
            }

            InlineFragment field = InteractiveField(dictionary);
            if (field.Plain.Length > 0)
                fields.Add(field);
        }

        if (fields.Count > 0)
        {
            return new InlineFragment(
                string.Join("; ", fields.Select(field => field.Markdown)),
                string.Join("; ", fields.Select(field => field.Plain)));
        }

        string? role = PdfStructureAttributes.Name(form, "PrintField", "Role");
        string? description = PdfStructureAttributes.Text(form, "PrintField", "Desc");
        string value = NormalizeText(SemanticText(form, skipLabels: true));

        if (role is "rb" or "cb")
        {
            string? state = PdfStructureAttributes.Name(form, "PrintField", "Checked") ??
                            PdfStructureAttributes.Name(form, "PrintField", "checked");
            bool on = state == "on";
            string label = NormalizeText(description ?? value);
            string plain = $"[{(on ? "x" : " ")}] {label}".TrimEnd();
            return new InlineFragment(MarkdownText.Escape(plain), plain);
        }

        if (description is { Length: > 0 } && value.Length > 0)
        {
            string plain = $"{NormalizeText(description)}: {value}";
            return new InlineFragment(MarkdownText.Escape(plain), plain);
        }

        string fallback = NormalizeText(description ?? value);
        return new InlineFragment(MarkdownText.Escape(fallback), fallback);
    }

    private InlineFragment InteractiveField(PdfDictionary widget)
    {
        string type = InheritedFieldValue(widget, PdfName.Get("FT")).AsName?.Value ?? string.Empty;
        PdfValue flagsValue = InheritedFieldValue(widget, PdfName.Get("Ff"));
        long flags = flagsValue.IsNumber ? flagsValue.AsInteger : 0;
        string label = FieldValueText(InheritedFieldValue(widget, PdfName.Get("TU"))) ??
                       widget.GetTextString(PdfName.Get("Contents")) ??
                       FieldValueText(InheritedFieldValue(widget, PdfName.Get("T"))) ??
                       "Field";
        PdfValue rawValue = InheritedFieldValue(widget, PdfName.Get("V"));
        string? value = FieldValueText(rawValue);
        string plain;

        if (type == "Btn" && (flags & (1 << 16)) == 0)
        {
            string? state = value ?? widget.GetName(PdfName.Get("AS"))?.Value;
            plain = $"[{(state is not null and not "Off" ? "x" : " ")}] {label}";
        }
        else if (type == "Btn")
        {
            plain = label;
        }
        else if (type == "Sig")
        {
            plain = $"{label}: {(rawValue.IsNull ? "[unsigned]" : "[signed]")}";
        }
        else if (type == "Tx" && (flags & (1 << 13)) != 0)
        {
            plain = $"{label}: [password]";
            _diagnostics.Add(
                MarkdownExportWarningKind.StructureApproximated,
                "A password field value was deliberately redacted from Markdown.",
                "Password field");
        }
        else
        {
            plain = value is { Length: > 0 } ? $"{label}: {value}" : label;
        }

        plain = NormalizeText(plain);
        return new InlineFragment(MarkdownText.Escape(plain), plain);
    }

    private static PdfValue InheritedFieldValue(PdfDictionary dictionary, PdfName key)
    {
        PdfDictionary? current = dictionary;
        for (int depth = 0; current is not null && depth < 32; depth++)
        {
            if (current.ContainsKey(key))
                return current.Get(key);
            current = current.GetDictionary(PdfName.Get("Parent"));
        }

        return PdfValue.Null;
    }

    private static string? FieldValueText(PdfValue value) => value.Kind switch
    {
        PdfValueKind.String => value.AsTextString,
        PdfValueKind.Name => value.AsName?.Value,
        PdfValueKind.Integer or PdfValueKind.Real => value.AsNumber.ToString(
            System.Globalization.CultureInfo.InvariantCulture),
        PdfValueKind.Array when value.AsArray is { } array => string.Join(
            ", ",
            Enumerable.Range(0, array.Count)
                .Select(index => FieldValueText(array.Get(index)))
                .Where(text => !string.IsNullOrEmpty(text))),
        _ => null,
    };

    private string SemanticText(PdfStructureNode node, bool skipLabels = false)
    {
        if (node.ActualText is { } actual)
            return ReplacementText(actual);

        var text = new StringBuilder();
        foreach (PdfStructureKid kid in node.Kids)
        {
            switch (kid)
            {
                case PdfStructureContentKid content:
                    text.Append(ContentText(content.Reference));
                    break;
                case PdfStructureElementKid element when
                    !skipLabels || element.Element.ResolvedTag != "Lbl":
                    text.Append(SemanticText(element.Element, skipLabels));
                    break;
            }
        }

        return text.ToString();
    }

    private static string TableCellMarkdown(string markdown) => markdown
        .Replace("|", "\\|", StringComparison.Ordinal)
        .Replace("  \n", "<br>", StringComparison.Ordinal)
        .Replace("\n", "<br>", StringComparison.Ordinal);

    private static string MarkdownUrlPath(string directory, string fileName)
    {
        IEnumerable<string> encoded = directory.Split('/').Append(fileName).Select(Uri.EscapeDataString);
        return string.Join('/', encoded);
    }

    private static void AppendInline(StringBuilder markdown, StringBuilder plain, InlineFragment fragment)
    {
        if (fragment.Plain.Length == 0)
            return;

        markdown.Append(fragment.Markdown);
        plain.Append(fragment.Plain);
    }

    private string ContentText(PdfContentReference reference)
    {
        var key = (reference.Page.Number, reference.Stream.Number, reference.MarkedContentId);
        if (_content.TryGetValue(key, out string? cached))
            return cached;

        if (!_pages.TryGetValue(reference.Page.Number, out PdfPage? page))
        {
            ReportSkipped("MCID", "A marked-content reference did not identify a page in this document.");
            return _content[key] = string.Empty;
        }

        if (!_letters.TryGetValue(reference.Page.Number, out IReadOnlyList<PdfLetter>? pageLetters))
        {
            try
            {
                pageLetters = page.ExtractLetters(new TextExtractionOptions
                {
                    SkipHiddenLayers = !_options.IncludeHiddenLayers,
                    UseActualText = true,
                });
            }
            catch (Exception exception) when (MarkdownExporter.IsRecoverableContentException(exception))
            {
                _skippedPages.Add(page.Id.Number);
                _diagnostics.Add(
                    MarkdownExportWarningKind.ContentSkipped,
                    "A page content stream could not be decoded or parsed and was skipped.",
                    $"Page {PageNumber(page)}");
                pageLetters = [];
            }
            _letters.Add(reference.Page.Number, pageLetters);
        }

        List<PdfLetter> selected = [];
        foreach (PdfLetter letter in pageLetters)
        {
            if (letter.MarkedContentId == reference.MarkedContentId &&
                letter.MarkedContentStream.Number == reference.Stream.Number)
                selected.Add(letter);
        }

        if (selected.Count == 0)
        {
            ReportSkipped(
                "MCID",
                "A marked-content reference had no extractable glyphs on its page.");
            return _content[key] = string.Empty;
        }

        _resolvedContent.Add(key);

        string composed = TextExtractor.Compose(selected, new TextExtractionOptions
        {
            ResolveBidirectional = true,
        });
        if (selected.Any(letter => letter.IsActualText))
        {
            composed = composed.ReplaceLineEndings("\n");
            if (composed.EndsWith('\n'))
                composed = composed[..^1];
            return _content[key] = ReplacementText(composed);
        }

        return _content[key] = NormalizeText(composed);
    }

    private int PageNumber(PdfPage page)
    {
        for (int index = 0; index < _document.Pages.Count; index++)
        {
            if (_document.Pages[index].Id.Number == page.Id.Number)
                return index + 1;
        }

        return 0;
    }

    private void ReportSkipped(string subject, string message) =>
        _diagnostics.Add(MarkdownExportWarningKind.ContentSkipped, message, subject);

    private void ReportNamespaceApproximation(PdfStructureNode node) =>
        _diagnostics.Add(
            MarkdownExportWarningKind.StructureApproximated,
            "A structure element in a non-PDF namespace had no standard role mapping and was flattened.",
            $"{node.ResolvedNamespaceName}#{node.ResolvedTag}");

    private static void AppendFragment(StringBuilder builder, string fragment)
    {
        fragment = NormalizeText(fragment);
        if (fragment.Length == 0)
            return;

        if (builder.Length > 0 && NeedsSeparator(builder[^1], fragment[0]))
            builder.Append(' ');
        builder.Append(fragment);
    }

    private static bool NeedsSeparator(char left, char right) =>
        !char.IsWhiteSpace(left) && !char.IsWhiteSpace(right) &&
        (char.IsLetterOrDigit(left) || left is ')' or ']' or '}') &&
        (char.IsLetterOrDigit(right) || right is '(' or '[' or '{');

    private static string NormalizeText(string text)
    {
        text = text.ReplaceLineEndings("\n").Trim();
        if (text.Length == 0)
            return string.Empty;

        var normalized = new StringBuilder(text.Length);
        bool whitespace = false;
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                whitespace = normalized.Length > 0;
                continue;
            }

            if (whitespace)
                normalized.Append(' ');
            normalized.Append(c);
            whitespace = false;
        }

        return normalized.ToString();
    }

    private static string ReplacementText(string text) =>
        text.ReplaceLineEndings("\n").Replace('\0', '\uFFFD');

    private static int? HeadingLevel(string tag) =>
        tag.Length == 2 && tag[0] == 'H' && tag[1] is >= '1' and <= '6'
            ? tag[1] - '0'
            : null;

    private static bool HasStandardPdfSemantics(PdfStructureNode node) =>
        node.ResolvedNamespaceName is null or "http://iso.org/pdf/ssn" or "http://iso.org/pdf2/ssn";

    private static int InferredHeadingLevel(PdfStructureNode node)
    {
        int level = 1;
        for (PdfStructureNode? parent = node.Parent; parent is not null; parent = parent.Parent)
        {
            if (parent.ResolvedTag is "Part" or "Art" or "Sect" or "Div")
                level++;
        }

        return Math.Min(level, 6);
    }

    private readonly record struct InlineFragment(string Markdown, string Plain);

    private readonly record struct TableCell(
        string Markdown,
        string Plain,
        bool Header,
        int RowSpan,
        int ColumnSpan);
}
