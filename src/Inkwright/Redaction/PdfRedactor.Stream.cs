using Inkwright.Content;
using Inkwright.Cos;
using Inkwright.Fonts;
using Inkwright.IO;

namespace Inkwright.Redaction;

internal static partial class PdfRedactor
{
    private const int MaxFormDepth = 12;

    /// <summary>Bounds the work a page can demand by drawing nested forms many times over.</summary>
    private const int MaxFormInvocations = 100_000;

    private sealed partial class StreamRewriter(
        PdfDocument document,
        PdfRectangle[] targets,
        bool removeImages)
    {
        private readonly Dictionary<PdfDictionary, LoadedFont> _fonts = [];
        private readonly Stack<GraphicsState> _stack = new();
        private readonly HashSet<PdfStream> _activeForms = new(ReferenceEqualityComparer.Instance);
        private GraphicsState _state = GraphicsState.Initial();
        private int _formInvocations;

        internal int GlyphsRemoved { get; private set; }

        internal int ImagesRemoved { get; private set; }

        internal byte[] Rewrite(
            ReadOnlySpan<byte> content,
            PdfDictionary resources,
            PdfObjectId resourcesOwner,
            PdfMatrix baseMatrix,
            int depth,
            long structParents)
        {
            if (depth > MaxFormDepth)
            {
                throw new PdfRedactionException(
                    $"Form XObjects are nested more than {MaxFormDepth} levels deep, so their content cannot be checked for the redaction regions.");
            }

            if (content.IsEmpty)
                return [];

            _state = _state with { Transform = baseMatrix * _state.Transform };
            ContentOperations operations = ContentOperations.Parse(content, document);

            using var buffer = new MemoryStream(content.Length);
            using var writer = new Utf8Writer(buffer);
            var context = new StreamContext(
                resources,
                resourcesOwner,
                depth,
                structParents,
                writer,
                new PdfObjectSerializer(writer));

            PdfMatrix textMatrix = PdfMatrix.Identity;
            PdfMatrix lineMatrix = PdfMatrix.Identity;

            for (int i = 0; i < operations.Count; i++)
            {
                int removedBefore = GlyphsRemoved + ImagesRemoved;
                ProcessOperation(operations, i, context, ref textMatrix, ref lineMatrix);

                if (GlyphsRemoved + ImagesRemoved > removedBefore)
                {
                    foreach (MarkedSequence sequence in context.OpenSequences)
                        sequence.Redacted = true;
                }
            }

            FinishOpenSequences(context);
            writer.Flush();
            return context.ApplySplices(buffer.ToArray());
        }

        private void ProcessOperation(
            ContentOperations operations,
            int index,
            StreamContext context,
            ref PdfMatrix textMatrix,
            ref PdfMatrix lineMatrix)
        {
            Utf8Writer writer = context.Writer;
            PdfObjectSerializer serializer = context.Serializer;
            PdfDictionary resources = context.Resources;
            PdfOperator op = operations.GetOperator(index);
            ReadOnlySpan<PdfValue> operands = operations.GetOperands(index);

            switch (op)
            {
                case PdfOperator.SaveState:
                    _stack.Push(_state);
                    WriteOperation(writer, serializer, op, operands, operations.GetUnknownName(index));
                    break;

                case PdfOperator.RestoreState:
                    if (_stack.Count > 0)
                        _state = _stack.Pop();
                    WriteOperation(writer, serializer, op, operands, operations.GetUnknownName(index));
                    break;

                case PdfOperator.ConcatMatrix when operands.Length >= 6:
                    _state = _state with { Transform = Matrix(operands) * _state.Transform };
                    WriteOperation(writer, serializer, op, operands, operations.GetUnknownName(index));
                    break;

                case PdfOperator.BeginText:
                    textMatrix = PdfMatrix.Identity;
                    lineMatrix = PdfMatrix.Identity;
                    WriteOperation(writer, serializer, op, operands, operations.GetUnknownName(index));
                    break;

                case PdfOperator.SetFont when operands.Length >= 2:
                    _state = _state with
                    {
                        Font = ResolveFont(resources, operands[0].AsName),
                        FontSize = operands[1].AsNumber,
                    };
                    WriteOperation(writer, serializer, op, operands, operations.GetUnknownName(index));
                    break;

                case PdfOperator.SetCharSpacing when operands.Length >= 1:
                    _state = _state with { CharSpacing = operands[0].AsNumber };
                    WriteOperation(writer, serializer, op, operands, operations.GetUnknownName(index));
                    break;

                case PdfOperator.SetWordSpacing when operands.Length >= 1:
                    _state = _state with { WordSpacing = operands[0].AsNumber };
                    WriteOperation(writer, serializer, op, operands, operations.GetUnknownName(index));
                    break;

                case PdfOperator.SetHorizontalScale when operands.Length >= 1:
                    _state = _state with { HorizontalScale = operands[0].AsNumber / 100.0 };
                    WriteOperation(writer, serializer, op, operands, operations.GetUnknownName(index));
                    break;

                case PdfOperator.SetLeading when operands.Length >= 1:
                    _state = _state with { Leading = operands[0].AsNumber };
                    WriteOperation(writer, serializer, op, operands, operations.GetUnknownName(index));
                    break;

                case PdfOperator.SetTextRise when operands.Length >= 1:
                    _state = _state with { Rise = operands[0].AsNumber };
                    WriteOperation(writer, serializer, op, operands, operations.GetUnknownName(index));
                    break;

                case PdfOperator.SetTextMatrix when operands.Length >= 6:
                    textMatrix = lineMatrix = Matrix(operands);
                    WriteOperation(writer, serializer, op, operands, operations.GetUnknownName(index));
                    break;

                case PdfOperator.TextMove when operands.Length >= 2:
                    lineMatrix = PdfMatrix.Translation(operands[0].AsNumber, operands[1].AsNumber) * lineMatrix;
                    textMatrix = lineMatrix;
                    WriteOperation(writer, serializer, op, operands, operations.GetUnknownName(index));
                    break;

                case PdfOperator.TextMoveSetLeading when operands.Length >= 2:
                    _state = _state with { Leading = -operands[1].AsNumber };
                    lineMatrix = PdfMatrix.Translation(operands[0].AsNumber, operands[1].AsNumber) * lineMatrix;
                    textMatrix = lineMatrix;
                    WriteOperation(writer, serializer, op, operands, operations.GetUnknownName(index));
                    break;

                case PdfOperator.TextNextLine:
                    lineMatrix = PdfMatrix.Translation(0, -_state.Leading) * lineMatrix;
                    textMatrix = lineMatrix;
                    WriteOperation(writer, serializer, op, operands, operations.GetUnknownName(index));
                    break;

                case PdfOperator.ShowText:
                case PdfOperator.ShowTextAdjusted:
                case PdfOperator.NextLineShowText:
                case PdfOperator.NextLineShowTextSpacing:
                    RewriteTextShowing(op, operands, writer, serializer, ref textMatrix, ref lineMatrix);
                    break;

                case PdfOperator.DrawXObject when operands.Length >= 1 && operands[0].AsName is { } name:
                    RewriteXObject(name, context);
                    break;

                case PdfOperator.BeginMarkedContent:
                case PdfOperator.BeginMarkedContentProperties:
                    BeginMarkedSequence(op, operands, context, operations.GetUnknownName(index));
                    break;

                case PdfOperator.EndMarkedContent:
                    WriteOperation(writer, serializer, op, operands, operations.GetUnknownName(index));
                    EndMarkedSequence(context);
                    break;

                case PdfOperator.SetExtGState when operands.Length >= 1:
                    ApplyExtGStateFont(resources, operands[0].AsName);
                    WriteOperation(writer, serializer, op, operands, operations.GetUnknownName(index));
                    break;

                case PdfOperator.InlineImage when operands.Length >= 1 && operands[^1].AsStream is { } inline:
                    RewriteInlineImage(inline, writer, serializer);
                    break;

                default:
                    WriteOperation(writer, serializer, op, operands, operations.GetUnknownName(index));
                    break;
            }
        }

        private void RewriteTextShowing(
            PdfOperator op,
            ReadOnlySpan<PdfValue> operands,
            Utf8Writer writer,
            PdfObjectSerializer serializer,
            ref PdfMatrix textMatrix,
            ref PdfMatrix lineMatrix)
        {
            if (op == PdfOperator.NextLineShowText && operands.Length >= 1)
            {
                lineMatrix = PdfMatrix.Translation(0, -_state.Leading) * lineMatrix;
                textMatrix = lineMatrix;
                WriteOperation(writer, serializer, PdfOperator.TextNextLine, [], "T*");
                FilterAndEmitSingleString(operands[0], writer, serializer, ref textMatrix);
                return;
            }

            if (op == PdfOperator.NextLineShowTextSpacing && operands.Length >= 3)
            {
                _state = _state with { WordSpacing = operands[0].AsNumber, CharSpacing = operands[1].AsNumber };
                lineMatrix = PdfMatrix.Translation(0, -_state.Leading) * lineMatrix;
                textMatrix = lineMatrix;
                WriteOperation(writer, serializer, PdfOperator.SetWordSpacing, operands[..1], "Tw");
                WriteOperation(writer, serializer, PdfOperator.SetCharSpacing, operands.Slice(1, 1), "Tc");
                WriteOperation(writer, serializer, PdfOperator.TextNextLine, [], "T*");
                FilterAndEmitSingleString(operands[2], writer, serializer, ref textMatrix);
                return;
            }

            if (op == PdfOperator.ShowText && operands.Length >= 1)
            {
                FilterAndEmitSingleString(operands[0], writer, serializer, ref textMatrix);
                return;
            }

            if (op == PdfOperator.ShowTextAdjusted && operands.Length >= 1 && operands[0].AsArray is { } array)
                FilterAndEmitAdjustedArray(array, writer, serializer, ref textMatrix);
        }

        private void FilterAndEmitSingleString(
            PdfValue strValue,
            Utf8Writer writer,
            PdfObjectSerializer serializer,
            ref PdfMatrix textMatrix)
        {
            if (strValue.Kind != PdfValueKind.String)
            {
                WriteOperation(writer, serializer, PdfOperator.ShowText, [strValue], "Tj");
                return;
            }

            if (_state.Font is not { } font)
            {
                if (strValue.AsStringBytes.Length > 0)
                    throw UnmeasurableText();

                WriteOperation(writer, serializer, PdfOperator.ShowText, [strValue], "Tj");
                return;
            }

            var parts = new PdfArray();
            int removed = FilterStringIntoParts(strValue.AsStringBytes, font, parts, ref textMatrix);
            if (removed == 0)
            {
                WriteOperation(writer, serializer, PdfOperator.ShowText, [strValue], "Tj");
            }
            else if (parts.Count > 0)
            {
                WriteOperation(writer, serializer, PdfOperator.ShowTextAdjusted, [PdfValue.Array(parts)], "TJ");
            }
        }

        private void FilterAndEmitAdjustedArray(
            PdfArray input,
            Utf8Writer writer,
            PdfObjectSerializer serializer,
            ref PdfMatrix textMatrix)
        {
            if (_state.Font is not { } font)
            {
                for (int i = 0; i < input.Count; i++)
                {
                    if (input.Get(i).Kind == PdfValueKind.String && input.Get(i).AsStringBytes.Length > 0)
                        throw UnmeasurableText();
                }

                WriteOperation(writer, serializer, PdfOperator.ShowTextAdjusted, [PdfValue.Array(input)], "TJ");
                return;
            }

            var output = new PdfArray();
            int removed = 0;
            for (int i = 0; i < input.Count; i++)
            {
                PdfValue item = input.Get(i);
                if (item.Kind == PdfValueKind.String)
                {
                    removed += FilterStringIntoParts(item.AsStringBytes, font, output, ref textMatrix);
                }
                else if (item.IsNumber)
                {
                    double shift = -item.AsNumber / 1000.0 * _state.FontSize * _state.HorizontalScale;
                    textMatrix = PdfMatrix.Translation(shift, 0) * textMatrix;
                    AppendAdjustment(output, item.AsNumber);
                }
            }

            if (removed == 0)
                WriteOperation(writer, serializer, PdfOperator.ShowTextAdjusted, [PdfValue.Array(input)], "TJ");
            else if (output.Count > 0)
                WriteOperation(writer, serializer, PdfOperator.ShowTextAdjusted, [PdfValue.Array(output)], "TJ");
        }

        private static PdfRedactionException UnmeasurableText() => new(
            "The page shows text in a font that cannot be resolved, so the position of that text is unknown and it cannot be checked for the redaction regions.");

        private int FilterStringIntoParts(
            ReadOnlySpan<byte> bytes,
            LoadedFont font,
            PdfArray output,
            ref PdfMatrix textMatrix)
        {
            List<byte> surviving = [];
            int removedCount = 0;

            for (int index = 0; index < bytes.Length;)
            {
                int start = index;
                int length = font.ReadCode(bytes, index, out uint code);
                index += length;

                double glyphWidth = font.GetWidth(code) / 1000.0;
                double width = glyphWidth * _state.FontSize;
                double spacing = _state.CharSpacing + (length == 1 && code == 32 ? _state.WordSpacing : 0.0);

                PdfRectangle bounds = ComputeGlyphBounds(glyphWidth, textMatrix);
                double tx = (width + spacing) * _state.HorizontalScale;
                textMatrix = PdfMatrix.Translation(tx, 0) * textMatrix;

                if (IntersectsAny(bounds, targets))
                {
                    FlushSurvivingBytes(surviving, output);
                    double adjustment = Math.Abs(_state.FontSize) > 1e-9
                        ? -1000.0 * (glyphWidth + (spacing / _state.FontSize))
                        : 0.0;
                    AppendAdjustment(output, adjustment);
                    removedCount++;
                    GlyphsRemoved++;
                }
                else
                {
                    for (int b = start; b < start + length; b++)
                        surviving.Add(bytes[b]);
                }
            }

            FlushSurvivingBytes(surviving, output);
            return removedCount;
        }

        private PdfRectangle ComputeGlyphBounds(double glyphWidth, PdfMatrix textMatrix)
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
            return PdfRectangle.FromSize(origin.X, origin.Y, drawn, height);
        }

        private static void FlushSurvivingBytes(List<byte> surviving, PdfArray output)
        {
            if (surviving.Count == 0)
                return;

            output.Add(PdfValue.String([.. surviving]));
            surviving.Clear();
        }

        private static void AppendAdjustment(PdfArray output, double adjustment)
        {
            if (Math.Abs(adjustment) < 1e-6)
                return;

            if (output.Count > 0 && output.Get(output.Count - 1).IsNumber)
            {
                double combined = output.Get(output.Count - 1).AsNumber + adjustment;
                output[output.Count - 1] = PdfValue.Number(combined);
                return;
            }

            output.Add(PdfValue.Number(adjustment));
        }
    }
}
