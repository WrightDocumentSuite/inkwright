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
        private void RewriteXObject(PdfName name, StreamContext context)
        {
            Utf8Writer writer = context.Writer;
            PdfObjectSerializer serializer = context.Serializer;
            PdfDictionary? xObjects = context.Resources.GetDictionary(PdfNames.XObject);
            PdfValue raw = xObjects?[name] ?? PdfValue.Null;
            PdfStream? stream = xObjects?.GetStream(name);
            PdfName? subtype = stream?.Dictionary.GetName(PdfNames.Subtype);

            if (subtype == PdfNames.Image)
            {
                if (removeImages && IntersectsAny(PdfRectangle.FromSize(0, 0, 1, 1).Transform(_state.Transform), targets))
                {
                    _staleEntries.Add(new ResourceEntry(xObjects!, name, CategoryOwner(context, PdfNames.XObject)));
                    ImagesRemoved++;
                    return;
                }

                WriteOperation(writer, serializer, PdfOperator.DrawXObject, [PdfValue.Name(name)], "Do");
                return;
            }

            PdfName drawn = subtype == PdfNames.Form && stream is not null
                ? RewriteFormXObject(stream, raw, name, xObjects!, context)
                : name;

            WriteOperation(writer, serializer, PdfOperator.DrawXObject, [PdfValue.Name(drawn)], "Do");
        }

        /// <summary>
        /// Rewrites one invocation of a form and returns the resource name that should draw it. Each
        /// invocation has its own position on the page, so a form that changes is copied for this
        /// invocation alone: every other place that draws it keeps the original.
        /// </summary>
        private PdfName RewriteFormXObject(
            PdfStream form,
            PdfValue rawReference,
            PdfName name,
            PdfDictionary xObjects,
            StreamContext parent)
        {
            if (!_activeForms.Add(form))
                throw new PdfRedactionException($"The Form XObject /{name.Value} draws itself, so its content cannot be redacted.");

            GraphicsState saved = _state;
            int savedDepth = _stack.Count;

            try
            {
                if (++_formInvocations > MaxFormInvocations)
                {
                    throw new PdfRedactionException(
                        $"The page draws Form XObjects more than {MaxFormInvocations} times, so its content cannot be checked for the redaction regions.");
                }

                PdfMatrix matrix = PdfMatrix.FromArray(form.Dictionary.GetArray(PdfNames.Matrix));
                PdfDictionary formResources = form.Dictionary.GetDictionary(PdfNames.Resources) ?? parent.Resources;
                int beforeGlyphs = GlyphsRemoved;
                int beforeImages = ImagesRemoved;

                byte[] rewritten = Rewrite(
                    form.DecodedSpan(),
                    formResources,
                    FormResourcesOwner(form, formResources, rawReference, parent),
                    matrix,
                    parent.Depth + 1,
                    form.Dictionary.GetInteger(PdfNames.StructParents, -1));

                if (GlyphsRemoved == beforeGlyphs && ImagesRemoved == beforeImages)
                    return name;

                long structParent = form.Dictionary.GetInteger(PdfNames.StructParent, -1);
                if (structParent >= 0)
                    _redactedObjects.Add(structParent);

                return ReplaceFormForThisInvocation(form, rewritten, rawReference, name, xObjects, parent);
            }
            finally
            {
                _activeForms.Remove(form);
                while (_stack.Count > savedDepth)
                    _stack.Pop();
                _state = saved;
            }
        }

        private static PdfObjectId FormResourcesOwner(
            PdfStream form,
            PdfDictionary formResources,
            PdfValue rawReference,
            StreamContext parent)
        {
            if (ReferenceEquals(formResources, parent.Resources))
                return parent.ResourcesOwner;

            if (form.Dictionary.TryGetRaw(PdfNames.Resources, out PdfValue rawResources) && rawResources.IsReference)
                return rawResources.AsReference;

            return rawReference.IsReference ? rawReference.AsReference : PdfObjectId.None;
        }

        private PdfName ReplaceFormForThisInvocation(
            PdfStream form,
            byte[] rewritten,
            PdfValue rawReference,
            PdfName name,
            PdfDictionary xObjects,
            StreamContext parent)
        {
            var updated = PdfStream.FromData(rewritten, compress: true);
            foreach (KeyValuePair<PdfName, PdfValue> entry in form.Dictionary)
            {
                if (entry.Key != PdfNames.Length && entry.Key != PdfNames.Filter && entry.Key != PdfNames.DecodeParms)
                    updated.Dictionary.Set(entry.Key, entry.Value);
            }

            PdfObjectId cloneId = document.AddObject(PdfValue.Stream(updated));
            PdfName newName = UniqueResourceName(xObjects, "RdFm");
            PdfObjectId owner = CategoryOwner(parent, PdfNames.XObject);
            xObjects.Set(newName, PdfValue.Reference(cloneId));
            document.MarkChanged(owner);

            _staleEntries.Add(new ResourceEntry(xObjects, name, owner));
            if (rawReference.IsReference)
                _replacements.Add(new FormReplacement(xObjects, newName, owner, rawReference.AsReference, cloneId, updated));

            return newName;
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

        /// <summary>An <c>ExtGState</c> can select the font instead of a <c>Tf</c> operator.</summary>
        private void ApplyExtGStateFont(PdfDictionary resources, PdfName? name)
        {
            if (name is null ||
                resources.GetDictionary(PdfNames.ExtGState)?.GetDictionary(name)?.GetArray(PdfNames.Font) is not { Count: >= 2 } entry ||
                entry.Get(0).AsDictionary is not { } dictionary)
            {
                return;
            }

            if (!_fonts.TryGetValue(dictionary, out LoadedFont? font))
            {
                font = LoadedFont.Read(dictionary);
                _fonts[dictionary] = font;
            }

            _state = _state with { Font = font, FontSize = entry.GetNumber(1) };
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
