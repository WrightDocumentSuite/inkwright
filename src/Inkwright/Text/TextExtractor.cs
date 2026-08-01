using System.Text;
using Inkwright.Content;
using Inkwright.Cos;
using Inkwright.Fonts;

namespace Inkwright.Text;

/// <summary>
/// Recovers text from a page by replaying its content stream and tracking the graphics state the
/// way a renderer would. Nothing is guessed from the byte stream: every glyph's position comes from
/// the text and transformation matrices that were actually in effect when it was painted.
/// </summary>
public static class TextExtractor
{
    private const int MaxFormDepth = 12;

    /// <summary>Extracts every glyph on a page, in the order the content stream painted them.</summary>
    /// <param name="page">The page to read.</param>
    /// <param name="options">What to do beyond reading the glyphs; the defaults do nothing extra.</param>
    public static IReadOnlyList<PdfLetter> ExtractLetters(PdfPage page, TextExtractionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(page);

        options ??= TextExtractionOptions.Default;

        var state = new ExtractionState(
            page.Document,
            options.SkipHiddenLayers ? page.Document.OptionalContent : null,
            options.UseActualText);

        state.Run(page.GetContent(), page.Resources, PdfMatrix.Identity, 0);

        return options.UseLogicalOrder ? Reorder(page, state.Letters) : state.Letters;
    }

    /// <summary>
    /// Puts glyphs into the order the structure tree declares. Sequences the tree names come first,
    /// in the order it names them; glyphs the tree says nothing about keep their painted order and
    /// follow, because dropping them would lose text that is merely untagged rather than decorative.
    /// </summary>
    private static List<PdfLetter> Reorder(PdfPage page, List<PdfLetter> letters)
    {
        IReadOnlyList<Tagging.PdfContentReference> order =
            Tagging.StructureReader.ReadingReferences(page.Document.Structure, page);
        if (order.Count == 0)
            return letters;

        var rank = new Dictionary<(int Stream, int Mcid), int>(order.Count);
        for (int i = 0; i < order.Count; i++)
            rank.TryAdd((order[i].Stream.Number, order[i].MarkedContentId), i);

        var groups = new SortedDictionary<int, List<PdfLetter>>();
        List<PdfLetter> untagged = [];

        foreach (PdfLetter letter in letters)
        {
            if (letter.MarkedContentId is { } mcid &&
                rank.TryGetValue((letter.MarkedContentStream.Number, mcid), out int position))
            {
                if (!groups.TryGetValue(position, out List<PdfLetter>? group))
                    groups[position] = group = [];

                group.Add(letter);
                continue;
            }

            untagged.Add(letter);
        }

        var ordered = new List<PdfLetter>(letters.Count);
        foreach (List<PdfLetter> group in groups.Values)
            ordered.AddRange(group);

        ordered.AddRange(untagged);
        return ordered;
    }

    /// <summary>Extracts the text of a page, grouping glyphs into words and lines by position.</summary>
    /// <param name="page">The page to read.</param>
    /// <param name="options">What to do beyond reading the glyphs; the defaults do nothing extra.</param>
    public static string ExtractText(PdfPage page, TextExtractionOptions? options = null) =>
        Compose(ExtractLetters(page, options), options);

    /// <summary>Groups letters into lines and joins them, inserting spaces where the gaps say so.</summary>
    /// <param name="letters">The letters to compose.</param>
    /// <param name="options">What to do beyond joining the glyphs; the defaults do nothing extra.</param>
    public static string Compose(IReadOnlyList<PdfLetter> letters, TextExtractionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(letters);

        if (letters.Count == 0)
            return string.Empty;

        options ??= TextExtractionOptions.Default;

        if (options.UseLogicalOrder)
            return ComposeInOrder(letters, options);

        // Group by baseline, allowing for the slight drift that superscripts and rounding introduce.
        List<List<PdfLetter>> lines = [];
        foreach (PdfLetter letter in letters)
        {
            double tolerance = Math.Max(1.0, letter.FontSize * 0.3);
            List<PdfLetter>? match = null;

            for (int i = lines.Count - 1; i >= 0 && i >= lines.Count - 4; i--)
            {
                if (Math.Abs(lines[i][0].Origin.Y - letter.Origin.Y) <= tolerance)
                {
                    match = lines[i];
                    break;
                }
            }

            if (match is null)
            {
                lines.Add([letter]);
                continue;
            }

            match.Add(letter);
        }

        lines.Sort((a, b) => b[0].Origin.Y.CompareTo(a[0].Origin.Y));

        var text = new StringBuilder(letters.Count + lines.Count);
        var current = new StringBuilder(64);

        foreach (List<PdfLetter> line in lines)
        {
            line.Sort((a, b) => a.Origin.X.CompareTo(b.Origin.X));
            current.Clear();

            double previousEnd = double.NaN;
            foreach (PdfLetter letter in line)
            {
                if (!double.IsNaN(previousEnd))
                {
                    double gap = letter.Origin.X - previousEnd;
                    if (gap > letter.FontSize * 0.18 && !letter.IsWhiteSpace && current.Length > 0 && current[^1] != ' ')
                        current.Append(' ');
                }

                current.Append(letter.Text);
                previousEnd = letter.Origin.X + letter.Width;
            }

            // The line has to be whole before it can be reordered, because which way a run reads
            // depends on the characters around it.
            text.Append(options.ResolveBidirectional ? Bidi.Resolve(current.ToString()).Text : current);
            text.Append('\n');
        }

        return text.ToString();
    }

    /// <summary>
    /// Joins glyphs that are already in reading order, so nothing is sorted by position: doing so
    /// would put a two-column page straight back into the order it was painted. A new line is begun
    /// wherever the structure moves to another marked-content sequence, or the glyphs move to
    /// another baseline within one.
    /// </summary>
    private static string ComposeInOrder(IReadOnlyList<PdfLetter> letters, TextExtractionOptions options)
    {
        var text = new StringBuilder(letters.Count + 16);
        var current = new StringBuilder(64);

        int? sequence = letters[0].MarkedContentId;
        PdfObjectId sequenceStream = letters[0].MarkedContentStream;
        double previousEnd = double.NaN;
        double baseline = double.NaN;

        foreach (PdfLetter letter in letters)
        {
            bool moved = !double.IsNaN(baseline) &&
                         Math.Abs(baseline - letter.Origin.Y) > Math.Max(1, letter.FontSize * 0.3);

            if (letter.MarkedContentId != sequence ||
                letter.MarkedContentStream != sequenceStream ||
                moved)
            {
                Flush(text, current, options);
                sequence = letter.MarkedContentId;
                sequenceStream = letter.MarkedContentStream;
                previousEnd = double.NaN;
            }

            if (!double.IsNaN(previousEnd))
            {
                double gap = letter.Origin.X - previousEnd;
                if (gap > letter.FontSize * 0.18 && !letter.IsWhiteSpace && current.Length > 0 && current[^1] != ' ')
                    current.Append(' ');
            }

            current.Append(letter.Text);
            previousEnd = letter.Origin.X + letter.Width;
            baseline = letter.Origin.Y;
        }

        Flush(text, current, options);
        return text.ToString();

        static void Flush(StringBuilder text, StringBuilder current, TextExtractionOptions options)
        {
            if (current.Length == 0)
                return;

            text.Append(options.ResolveBidirectional ? Bidi.Resolve(current.ToString()).Text : current);
            text.Append('\n');
            current.Clear();
        }
    }

    private sealed class ExtractionState(
        PdfDocument document,
        PdfOptionalContent? layers,
        bool useActualText)
    {
        private readonly Dictionary<PdfDictionary, LoadedFont> _fonts = [];
        private readonly Stack<GraphicsState> _stack = new();
        private GraphicsState _state = GraphicsState.Initial;

        /// <summary>How many marked-content sequences deep the hidden one we are inside started.</summary>
        private int _hiddenDepth;
        private int _markedDepth;

        /// <summary>
        /// The identifier of each open marked-content sequence, innermost last. A sequence that
        /// states no identifier of its own inherits the one it sits inside.
        /// </summary>
        private readonly Stack<MarkedContentContext?> _marked = new();
        private readonly Stack<ActualTextFrame?> _actualText = new();
        private PdfObjectId _contentStream;

        internal List<PdfLetter> Letters { get; } = [];

        internal void Run(ReadOnlySpan<byte> content, PdfDictionary resources, PdfMatrix baseMatrix, int depth)
        {
            if (depth > MaxFormDepth)
                return;

            _state = _state with { Transform = baseMatrix * _state.Transform };
            ContentOperations operations = ContentOperations.Parse(content, document);

            PdfMatrix textMatrix = PdfMatrix.Identity;
            PdfMatrix lineMatrix = PdfMatrix.Identity;

            for (int i = 0; i < operations.Count; i++)
            {
                ReadOnlySpan<PdfValue> operands = operations.GetOperands(i);

                switch (operations.GetOperator(i))
                {
                    case PdfOperator.SaveState:
                        _stack.Push(_state);
                        break;

                    case PdfOperator.RestoreState:
                        if (_stack.Count > 0)
                            _state = _stack.Pop();

                        break;

                    case PdfOperator.ConcatMatrix when operands.Length >= 6:
                        _state = _state with { Transform = Matrix(operands) * _state.Transform };
                        break;

                    case PdfOperator.BeginText:
                        textMatrix = PdfMatrix.Identity;
                        lineMatrix = PdfMatrix.Identity;
                        break;

                    case PdfOperator.SetFont when operands.Length >= 2:
                        _state = _state with
                        {
                            Font = ResolveFont(resources, operands[0].AsName),
                            FontName = operands[0].AsName?.Value ?? string.Empty,
                            FontSize = operands[1].AsNumber,
                        };
                        break;

                    case PdfOperator.SetCharSpacing when operands.Length >= 1:
                        _state = _state with { CharSpacing = operands[0].AsNumber };
                        break;

                    case PdfOperator.SetWordSpacing when operands.Length >= 1:
                        _state = _state with { WordSpacing = operands[0].AsNumber };
                        break;

                    case PdfOperator.SetHorizontalScale when operands.Length >= 1:
                        _state = _state with { HorizontalScale = operands[0].AsNumber / 100 };
                        break;

                    case PdfOperator.SetLeading when operands.Length >= 1:
                        _state = _state with { Leading = operands[0].AsNumber };
                        break;

                    case PdfOperator.SetTextRise when operands.Length >= 1:
                        _state = _state with { Rise = operands[0].AsNumber };
                        break;

                    case PdfOperator.SetTextRenderMode when operands.Length >= 1:
                        _state = _state with { RenderMode = (int)operands[0].AsInteger };
                        break;

                    case PdfOperator.SetTextMatrix when operands.Length >= 6:
                        textMatrix = lineMatrix = Matrix(operands);
                        break;

                    case PdfOperator.TextMove when operands.Length >= 2:
                        lineMatrix = PdfMatrix.Translation(operands[0].AsNumber, operands[1].AsNumber) * lineMatrix;
                        textMatrix = lineMatrix;
                        break;

                    case PdfOperator.TextMoveSetLeading when operands.Length >= 2:
                        _state = _state with { Leading = -operands[1].AsNumber };
                        lineMatrix = PdfMatrix.Translation(operands[0].AsNumber, operands[1].AsNumber) * lineMatrix;
                        textMatrix = lineMatrix;
                        break;

                    case PdfOperator.TextNextLine:
                        lineMatrix = PdfMatrix.Translation(0, -_state.Leading) * lineMatrix;
                        textMatrix = lineMatrix;
                        break;

                    case PdfOperator.ShowText when operands.Length >= 1:
                        Show(operands[0], ref textMatrix);
                        break;

                    case PdfOperator.NextLineShowText when operands.Length >= 1:
                        lineMatrix = PdfMatrix.Translation(0, -_state.Leading) * lineMatrix;
                        textMatrix = lineMatrix;
                        Show(operands[0], ref textMatrix);
                        break;

                    case PdfOperator.NextLineShowTextSpacing when operands.Length >= 3:
                        _state = _state with { WordSpacing = operands[0].AsNumber, CharSpacing = operands[1].AsNumber };
                        lineMatrix = PdfMatrix.Translation(0, -_state.Leading) * lineMatrix;
                        textMatrix = lineMatrix;
                        Show(operands[2], ref textMatrix);
                        break;

                    case PdfOperator.ShowTextAdjusted when operands.Length >= 1:
                        ShowAdjusted(operands[0].AsArray, ref textMatrix);
                        break;

                    case PdfOperator.DrawXObject when operands.Length >= 1:
                        RunForm(resources, operands[0].AsName, depth);
                        break;

                    case PdfOperator.BeginMarkedContent:
                    case PdfOperator.BeginMarkedContentProperties:
                        EnterMarkedContent(operations.GetOperator(i), operands, resources);
                        break;

                    case PdfOperator.EndMarkedContent:
                        LeaveMarkedContent();
                        break;

                    default:
                        break;
                }
            }
        }

        /// <summary>
        /// Tracks marked-content nesting so that a sequence tagged with a hidden layer suppresses
        /// everything up to its matching <c>EMC</c>, however many plain sequences sit inside it.
        /// </summary>
        private void EnterMarkedContent(PdfOperator @operator, ReadOnlySpan<PdfValue> operands, PdfDictionary resources)
        {
            _markedDepth++;

            PdfValue properties = @operator == PdfOperator.BeginMarkedContentProperties && operands.Length >= 2
                ? Resolve(operands[1], resources)
                : PdfValue.Null;

            MarkedContentContext? inherited = _marked.Count > 0 ? _marked.Peek() : null;
            PdfValue markedContentId = properties.AsDictionary?.Get(PdfNames.MCID) ?? PdfValue.Null;
            MarkedContentContext? context = markedContentId.Kind == PdfValueKind.Integer &&
                                            markedContentId.AsInteger is >= 0 and <= int.MaxValue
                ? new MarkedContentContext((int)markedContentId.AsInteger, _contentStream)
                : inherited;

            _marked.Push(context);

            if (layers is not null && _hiddenDepth == 0 && !properties.IsNull &&
                operands.Length >= 2 && operands[0].AsName == PdfNames.OC &&
                !layers.IsVisible(Raw(operands[1], resources)))
            {
                _hiddenDepth = _markedDepth;
            }

            string? replacement = useActualText && _hiddenDepth == 0
                ? properties.AsDictionary?.GetTextString(PdfNames.ActualText)
                : null;
            _actualText.Push(replacement is null
                ? null
                : new ActualTextFrame(Letters.Count, context, replacement));
        }

        private void LeaveMarkedContent()
        {
            if (_actualText.Count > 0 && _actualText.Pop() is { } replacement)
                ApplyActualText(replacement);

            if (_hiddenDepth == _markedDepth)
                _hiddenDepth = 0;

            if (_markedDepth > 0)
                _markedDepth--;

            if (_marked.Count > 0)
                _marked.Pop();
        }

        private void ApplyActualText(ActualTextFrame replacement)
        {
            int count = Math.Max(0, Letters.Count - replacement.Start);
            PdfLetter basis;
            PdfPoint origin;
            double width;

            if (count > 0)
            {
                basis = Letters[replacement.Start];
                origin = basis.Origin;
                width = 0;
                for (int i = replacement.Start; i < Letters.Count; i++)
                    width += Letters[i].Width;
                Letters.RemoveRange(replacement.Start, count);
            }
            else if (replacement.Start > 0)
            {
                basis = Letters[replacement.Start - 1];
                origin = basis.End;
                width = 0;
            }
            else
            {
                basis = new PdfLetter(
                    string.Empty,
                    PdfPoint.Origin,
                    0,
                    Math.Abs(_state.FontSize),
                    Math.Abs(_state.FontSize),
                    _state.FontName,
                    0);
                origin = basis.Origin;
                width = 0;
            }

            if (replacement.Text.Length == 0)
                return;

            Letters.Add(new PdfLetter(
                replacement.Text,
                origin,
                width,
                basis.Height,
                basis.FontSize,
                basis.FontName,
                basis.Angle)
            {
                MarkedContentId = replacement.Context?.MarkedContentId,
                MarkedContentStream = replacement.Context?.Stream ?? PdfObjectId.None,
                IsActualText = true,
            });
        }

        /// <summary>The property list an operand names, given inline or through the page's resources.</summary>
        private static PdfValue Resolve(PdfValue operand, PdfDictionary resources) =>
            operand.AsName is { } named
                ? resources.GetDictionary(PdfName.Get("Properties"))?.Get(named) ?? PdfValue.Null
                : operand;

        /// <summary>
        /// The same property list, left unresolved. Which optional content group governs a sequence
        /// is a question of object identity, so the reference itself is what the layers are asked about.
        /// </summary>
        private static PdfValue Raw(PdfValue operand, PdfDictionary resources) =>
            operand.AsName is { } named
                ? resources.GetDictionary(PdfName.Get("Properties"))?[named] ?? PdfValue.Null
                : operand;

        private void RunForm(PdfDictionary resources, PdfName? name, int depth)
        {
            if (name is null)
                return;

            PdfDictionary? xObjects = resources.GetDictionary(PdfNames.XObject);
            PdfValue rawForm = xObjects?[name] ?? PdfValue.Null;
            PdfStream? form = xObjects?.GetStream(name);
            if (form is null || form.Dictionary.GetName(PdfNames.Subtype) != PdfNames.Form)
                return;

            // A form can carry its own visibility, which hides everything it paints (§8.11.3.2).
            if (layers is not null && form.Dictionary[PdfNames.OC] is { IsNull: false } group &&
                !layers.IsVisible(group))
            {
                return;
            }

            GraphicsState saved = _state;
            int savedDepth = _stack.Count;
            int savedMarkedCount = _marked.Count;
            int savedActualTextCount = _actualText.Count;
            int savedMarkedDepth = _markedDepth;
            int savedHiddenDepth = _hiddenDepth;
            PdfObjectId savedContentStream = _contentStream;

            PdfMatrix matrix = PdfMatrix.FromArray(form.Dictionary.GetArray(PdfNames.Matrix));
            PdfDictionary formResources = form.Dictionary.GetDictionary(PdfNames.Resources) ?? resources;
            _contentStream = rawForm.IsReference ? rawForm.AsReference : PdfObjectId.None;
            try
            {
                Run(form.DecodedSpan(), formResources, matrix, depth + 1);
            }
            finally
            {
                while (_stack.Count > savedDepth)
                    _stack.Pop();
                while (_marked.Count > savedMarkedCount)
                    _marked.Pop();
                while (_actualText.Count > savedActualTextCount)
                    _actualText.Pop();

                _state = saved;
                _markedDepth = savedMarkedDepth;
                _hiddenDepth = savedHiddenDepth;
                _contentStream = savedContentStream;
            }
        }

        private LoadedFont? ResolveFont(PdfDictionary resources, PdfName? name)
        {
            if (name is null)
                return null;

            PdfDictionary? dictionary = resources.GetDictionary(PdfNames.Font)?.GetDictionary(name);
            if (dictionary is null)
                return null;

            if (_fonts.TryGetValue(dictionary, out LoadedFont? cached))
                return cached;

            LoadedFont font = LoadedFont.Read(dictionary);
            _fonts[dictionary] = font;
            return font;
        }

        private void ShowAdjusted(PdfArray? parts, ref PdfMatrix textMatrix)
        {
            if (parts is null)
                return;

            for (int i = 0; i < parts.Count; i++)
            {
                PdfValue part = parts.Get(i);

                if (part.Kind == PdfValueKind.String)
                {
                    Show(part, ref textMatrix);
                    continue;
                }

                if (!part.IsNumber)
                    continue;

                double shift = -part.AsNumber / 1000 * _state.FontSize * _state.HorizontalScale;
                textMatrix = PdfMatrix.Translation(shift, 0) * textMatrix;
            }
        }

        private void Show(PdfValue value, ref PdfMatrix textMatrix)
        {
            if (value.Kind != PdfValueKind.String || _state.Font is not { } font)
                return;

            ReadOnlySpan<byte> bytes = value.AsStringBytes;
            bool invisible = _state.RenderMode == 3 || _hiddenDepth > 0;

            for (int index = 0; index < bytes.Length;)
            {
                int length = font.ReadCode(bytes, index, out uint code);
                index += length;

                double glyphWidth = font.GetWidth(code) / 1000;
                double width = glyphWidth * _state.FontSize;
                double spacing = _state.CharSpacing;

                // Word spacing applies only to the single-byte code 32 (§9.3.3).
                if (length == 1 && code == 32)
                    spacing += _state.WordSpacing;

                if (!invisible && font.GetText(code) is { Length: > 0 } text)
                {
                    PdfMatrix render = new PdfMatrix(
                        _state.FontSize * _state.HorizontalScale, 0,
                        0, _state.FontSize,
                        0, _state.Rise) * textMatrix * _state.Transform;

                    PdfPoint origin = render.Transform(PdfPoint.Origin);
                    PdfPoint advance = render.Transform(new PdfPoint(glyphWidth, 0));
                    PdfPoint up = render.Transform(new PdfPoint(0, 1));

                    double drawn = origin.DistanceTo(advance);
                    double height = origin.DistanceTo(up);
                    double angle = double.RadiansToDegrees(Math.Atan2(advance.Y - origin.Y, advance.X - origin.X));

                    MarkedContentContext? context = _marked.Count > 0 ? _marked.Peek() : null;
                    Letters.Add(new PdfLetter(
                        text, origin, drawn, height, height, _state.FontName, double.IsNaN(angle) ? 0 : angle)
                    {
                        MarkedContentId = context?.MarkedContentId,
                        MarkedContentStream = context?.Stream ?? PdfObjectId.None,
                    });
                }

                double tx = (width + spacing) * _state.HorizontalScale;
                textMatrix = PdfMatrix.Translation(tx, 0) * textMatrix;
            }
        }

        private static PdfMatrix Matrix(ReadOnlySpan<PdfValue> operands) => new(
            operands[0].AsNumber, operands[1].AsNumber, operands[2].AsNumber,
            operands[3].AsNumber, operands[4].AsNumber, operands[5].AsNumber);

        private readonly record struct MarkedContentContext(int MarkedContentId, PdfObjectId Stream);

        private readonly record struct ActualTextFrame(
            int Start,
            MarkedContentContext? Context,
            string Text);
    }

    private readonly record struct GraphicsState(
        PdfMatrix Transform,
        LoadedFont? Font,
        string FontName,
        double FontSize,
        double CharSpacing,
        double WordSpacing,
        double HorizontalScale,
        double Leading,
        double Rise,
        int RenderMode)
    {
        internal static GraphicsState Initial =>
            new(PdfMatrix.Identity, null, string.Empty, 0, 0, 0, 1, 0, 0, 0);
    }
}
