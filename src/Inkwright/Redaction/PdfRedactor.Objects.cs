using System.Text;
using Inkwright.Content;
using Inkwright.Cos;
using Inkwright.Fonts;
using Inkwright.IO;

namespace Inkwright.Redaction;

internal static partial class PdfRedactor
{
    private sealed partial class StreamRewriter
    {
        private void RewriteXObject(
            PdfName name,
            PdfDictionary resources,
            int depth,
            Utf8Writer writer,
            PdfObjectSerializer serializer,
            HashSet<PdfName> survivingImages,
            HashSet<PdfName> removedImages)
        {
            PdfDictionary? xObjects = resources.GetDictionary(PdfNames.XObject);
            PdfValue raw = xObjects?[name] ?? PdfValue.Null;
            PdfStream? stream = xObjects?.GetStream(name);
            PdfName? subtype = stream?.Dictionary.GetName(PdfNames.Subtype);

            if (subtype == PdfNames.Image)
            {
                if (removeImages && IntersectsAny(PdfRectangle.FromSize(0, 0, 1, 1).Transform(_state.Transform), targets))
                {
                    removedImages.Add(name);
                    ImagesRemoved++;
                    return;
                }

                survivingImages.Add(name);
                WriteOperation(writer, serializer, PdfOperator.DrawXObject, [PdfValue.Name(name)], "Do");
                return;
            }

            if (subtype == PdfNames.Form && stream is not null)
                RewriteFormXObject(stream, raw, resources, depth);

            WriteOperation(writer, serializer, PdfOperator.DrawXObject, [PdfValue.Name(name)], "Do");
        }

        private void RewriteFormXObject(
            PdfStream form,
            PdfValue rawReference,
            PdfDictionary parentResources,
            int depth)
        {
            GraphicsState saved = _state;
            int savedDepth = _stack.Count;

            try
            {
                PdfMatrix matrix = PdfMatrix.FromArray(form.Dictionary.GetArray(PdfNames.Matrix));
                PdfDictionary formResources = form.Dictionary.GetDictionary(PdfNames.Resources) ?? parentResources;
                int beforeGlyphs = GlyphsRemoved;
                int beforeImages = ImagesRemoved;

                byte[] rewritten = Rewrite(form.DecodedSpan(), formResources, matrix, depth + 1);
                if (GlyphsRemoved > beforeGlyphs || ImagesRemoved > beforeImages)
                {
                    var updated = PdfStream.FromData(rewritten, compress: true);
                    foreach (KeyValuePair<PdfName, PdfValue> entry in form.Dictionary)
                    {
                        if (entry.Key != PdfNames.Length && entry.Key != PdfNames.Filter && entry.Key != PdfNames.DecodeParms)
                            updated.Dictionary.Set(entry.Key, entry.Value);
                    }

                    if (rawReference.IsReference)
                    {
                        document.SetObject(rawReference.AsReference, PdfValue.Stream(updated));
                        document.MarkChanged(rawReference.AsReference);
                    }
                }
            }
            finally
            {
                while (_stack.Count > savedDepth)
                    _stack.Pop();
                _state = saved;
            }
        }

        private void RewriteInlineImage(PdfStream inline, Utf8Writer writer, PdfObjectSerializer serializer)
        {
            if (removeImages && IntersectsAny(PdfRectangle.FromSize(0, 0, 1, 1).Transform(_state.Transform), targets))
            {
                ImagesRemoved++;
                return;
            }

            writer.Write("BI\n"u8);
            foreach (KeyValuePair<PdfName, PdfValue> entry in inline.Dictionary)
            {
                serializer.WriteValue(PdfValue.Name(entry.Key));
                writer.WriteSpace();
                serializer.WriteValue(entry.Value);
                writer.WriteNewLine();
            }

            writer.Write("ID\n"u8);
            writer.Write(inline.RawData.Span);
            writer.Write("\nEI\n"u8);
        }

        private void PruneRemovedImageResources(
            PdfDictionary resources,
            HashSet<PdfName> survivingImages,
            HashSet<PdfName> removedImages)
        {
            if (removedImages.Count == 0 || resources.GetDictionary(PdfNames.XObject) is not { } xObjects)
                return;

            foreach (PdfName removed in removedImages)
            {
                if (!survivingImages.Contains(removed))
                    xObjects.Remove(removed);
            }
        }

        private LoadedFont? ResolveFont(PdfDictionary resources, PdfName? name)
        {
            if (name is null || resources.GetDictionary(PdfNames.Font)?.GetDictionary(name) is not { } dictionary)
                return null;

            if (_fonts.TryGetValue(dictionary, out LoadedFont? cached))
                return cached;

            LoadedFont font = LoadedFont.Read(dictionary);
            _fonts[dictionary] = font;
            return font;
        }

        private static void WriteOperation(
            Utf8Writer writer,
            PdfObjectSerializer serializer,
            PdfOperator op,
            ReadOnlySpan<PdfValue> operands,
            string unknownName)
        {
            for (int i = 0; i < operands.Length; i++)
            {
                serializer.WriteValue(operands[i]);
                writer.WriteSpace();
            }

            if (op == PdfOperator.Unknown)
                writer.Write(Encoding.ASCII.GetBytes(unknownName));
            else
                writer.Write(PdfOperators.Bytes(op));

            writer.WriteNewLine();
        }

        private static PdfMatrix Matrix(ReadOnlySpan<PdfValue> operands) => new(
            operands[0].AsNumber,
            operands[1].AsNumber,
            operands[2].AsNumber,
            operands[3].AsNumber,
            operands[4].AsNumber,
            operands[5].AsNumber);
    }

    private readonly record struct GraphicsState(
        PdfMatrix Transform,
        LoadedFont? Font,
        double FontSize,
        double CharSpacing,
        double WordSpacing,
        double HorizontalScale,
        double Leading,
        double Rise)
    {
        internal static GraphicsState Initial() => new(
            PdfMatrix.Identity,
            null,
            0,
            0,
            0,
            1.0,
            0,
            0);
    }
}
