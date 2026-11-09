using Inkwright.Annotations;
using Inkwright.Cos;
using Inkwright.Forms;

namespace Inkwright.Signing;

/// <summary>
/// Evaluates whether incremental updates appended after a signature's byte range comply with the
/// <c>DocMDP</c> certification permissions (ISO 32000-2 §12.8.2.2) and <c>FieldMDP</c> locks
/// (§12.8.2.4) in effect when the signature was created.
/// </summary>
internal static class PdfModificationVerifier
{
    private static readonly PdfName[] PageGraphKeys =
        [PdfName.Get("Resources"), PdfName.Get("Group"), PdfName.Get("UserUnit")];

    private static readonly PdfName AppearanceKey = PdfName.Get("AP");

    internal static PdfFieldLock? ReadFieldLock(PdfDictionary? fieldDictionary, PdfDictionary signature)
    {
        PdfCertificationLevel? lockPermissions = null;
        if (fieldDictionary?.GetDictionary(PdfName.Get("Lock")) is { } lockDict)
        {
            long p = lockDict.GetInteger(PdfName.Get("P"));
            if (p is >= 1 and <= 3)
                lockPermissions = (PdfCertificationLevel)p;

            if (ParseLockDictionary(lockDict, lockPermissions) is { } fromField)
                return fromField;
        }

        if (signature.GetArray(PdfName.Get("Reference")) is not { } references)
            return null;

        for (int i = 0; i < references.Count; i++)
        {
            if (references.GetDictionary(i) is not { } reference ||
                reference.GetName(PdfName.Get("TransformMethod"))?.Value != "FieldMDP")
            {
                continue;
            }

            if (reference.GetDictionary(PdfName.Get("TransformParams")) is { } parameters &&
                ParseLockDictionary(parameters, lockPermissions) is { } fromRef)
            {
                return fromRef;
            }
        }

        return null;
    }

    private static PdfFieldLock? ParseLockDictionary(
        PdfDictionary dictionary,
        PdfCertificationLevel? permissions)
    {
        string? action = dictionary.GetName(PdfName.Get("Action"))?.Value;
        if (action == "All")
            return PdfFieldLock.All() with { Permissions = permissions };

        if (action is not ("Include" or "Exclude"))
            return null;

        if (dictionary.GetArray(PdfName.Get("Fields")) is not { } fieldsArray)
            return null;

        List<string> names = [];
        for (int i = 0; i < fieldsArray.Count; i++)
        {
            if (fieldsArray.Get(i).AsTextString is { Length: > 0 } name)
                names.Add(name);
        }

        if (names.Count == 0)
            return null;

        PdfFieldLock parsed = action == "Include"
            ? PdfFieldLock.Include(names)
            : PdfFieldLock.Exclude(names);
        return parsed with { Permissions = permissions };
    }

    internal static IReadOnlyList<string> AnalyzeSubsequentChanges(
        string signatureFieldName,
        PdfCertificationLevel? certificationLevel,
        PdfFieldLock? fieldLock,
        IReadOnlyList<(long Offset, long Length)> parts,
        byte[] fullFile,
        PdfDocument currentDocument)
    {
        if (parts.Count < 2)
            return ["The signature byte range is incomplete."];

        long signedEnd = parts[^1].Offset + parts[^1].Length;
        if (signedEnd >= fullFile.Length)
            return [];

        if (signedEnd <= 0 || signedEnd > int.MaxValue || !EndsWithEof(fullFile.AsSpan(0, (int)signedEnd)))
            return ["Trailing bytes after the signature do not form a valid incremental revision."];

        PdfDocument signedSnapshot;
        try
        {
            signedSnapshot = PdfDocument.Load(fullFile.AsSpan(0, (int)signedEnd).ToArray());
        }
        catch (Exception)
        {
            return ["The signed revision prefix could not be parsed as a valid PDF document."];
        }

        using (signedSnapshot)
        {
            List<string> violations = [];
            PdfCertificationLevel? effectiveLevel = EffectiveDocMdpLevel(certificationLevel, fieldLock);

            if (effectiveLevel is { } level and not PdfCertificationLevel.None)
                CheckDocMdp(level, signedSnapshot, currentDocument, violations);

            if (fieldLock is not null)
                CheckFieldMdp(signatureFieldName, fieldLock, signedSnapshot, currentDocument, violations);

            return violations;
        }
    }

    private static PdfCertificationLevel? EffectiveDocMdpLevel(
        PdfCertificationLevel? certificationLevel,
        PdfFieldLock? fieldLock)
    {
        PdfCertificationLevel? lockLevel = fieldLock?.Permissions;
        if (certificationLevel is null or PdfCertificationLevel.None)
            return lockLevel;
        if (lockLevel is null or PdfCertificationLevel.None)
            return certificationLevel;

        return (PdfCertificationLevel)Math.Min((int)certificationLevel.Value, (int)lockLevel.Value);
    }

    private static void CheckDocMdp(
        PdfCertificationLevel level,
        PdfDocument before,
        PdfDocument after,
        List<string> violations)
    {
        if (before.Pages.Count != after.Pages.Count)
        {
            violations.Add($"Page count changed from {before.Pages.Count} to {after.Pages.Count}, violating DocMDP ({level}).");
            return;
        }

        for (int i = 0; i < before.Pages.Count; i++)
            CheckPageChanges(i, level, before.Pages[i], after.Pages[i], violations);

        if (level == PdfCertificationLevel.NoChanges)
            CheckNoChangesFormMutations(before, after, violations);
    }

    private static void CheckPageChanges(
        int pageIndex,
        PdfCertificationLevel level,
        PdfPage beforePage,
        PdfPage afterPage,
        List<string> violations)
    {
        if (beforePage.MediaBox != afterPage.MediaBox ||
            beforePage.CropBox != afterPage.CropBox ||
            beforePage.Rotation != afterPage.Rotation)
        {
            violations.Add($"Page {pageIndex + 1} geometry changed after certification ({level}).");
        }

        if (!beforePage.GetContent().AsSpan().SequenceEqual(afterPage.GetContent()))
            violations.Add($"Page {pageIndex + 1} content stream changed after certification ({level}).");

        CheckPageGraph(pageIndex, level, beforePage, afterPage, violations);

        if (level is PdfCertificationLevel.NoChanges or PdfCertificationLevel.FormFilling)
            CheckPageAnnotations(pageIndex, level, beforePage, afterPage, violations);
    }

    /// <summary>
    /// A page's content stream only names what it draws. The forms, images and fonts behind those
    /// names decide what actually appears, so changing one changes the page as surely as editing
    /// its content stream does.
    /// </summary>
    private static void CheckPageGraph(
        int pageIndex,
        PdfCertificationLevel level,
        PdfPage beforePage,
        PdfPage afterPage,
        List<string> violations)
    {
        foreach (PdfName key in PageGraphKeys)
        {
            PdfGraphComparer.Outcome outcome = PdfGraphComparer.Compare(
                beforePage.Dictionary.Get(key),
                afterPage.Dictionary.Get(key));

            if (outcome == PdfGraphComparer.Outcome.Different)
                violations.Add($"Page {pageIndex + 1} /{key.Value} changed after certification ({level}).");
            else if (outcome == PdfGraphComparer.Outcome.TooLargeToCompare)
                violations.Add($"Page {pageIndex + 1} /{key.Value} is too large to confirm it is unchanged ({level}).");
        }
    }

    private static void CheckPageAnnotations(
        int pageIndex,
        PdfCertificationLevel level,
        PdfPage beforePage,
        PdfPage afterPage,
        List<string> violations)
    {
        List<PdfAnnotation> beforeNonWidgets = [.. beforePage.Annotations.Where(a => a.Type != PdfAnnotationType.Widget)];
        List<PdfAnnotation> afterNonWidgets = [.. afterPage.Annotations.Where(a => a.Type != PdfAnnotationType.Widget)];

        if (beforeNonWidgets.Count != afterNonWidgets.Count)
        {
            violations.Add($"Page {pageIndex + 1} annotations were added or removed, violating DocMDP ({level}).");
            return;
        }

        for (int i = 0; i < beforeNonWidgets.Count; i++)
        {
            PdfAnnotation a = beforeNonWidgets[i];
            PdfAnnotation b = afterNonWidgets[i];
            if (a.Type != b.Type || a.Rectangle != b.Rectangle || a.Contents != b.Contents ||
                PdfGraphComparer.Compare(a.Dictionary.Get(AppearanceKey), b.Dictionary.Get(AppearanceKey)) != PdfGraphComparer.Outcome.Equal)
            {
                violations.Add($"Page {pageIndex + 1} annotation '{a.Subtype}' was modified, violating DocMDP ({level}).");
                return;
            }
        }
    }

    private static void CheckNoChangesFormMutations(
        PdfDocument before,
        PdfDocument after,
        List<string> violations)
    {
        PdfForm? beforeForm = PdfForm.Open(before);
        PdfForm? afterForm = PdfForm.Open(after);
        if (afterForm is null)
            return;

        Dictionary<string, PdfFormField> beforeMap = beforeForm?.ToDictionary(f => f.FullName, StringComparer.Ordinal) ?? [];

        foreach (PdfFormField afterField in afterForm)
        {
            if (IsDocumentTimestampField(afterField))
                continue;

            if (!beforeMap.TryGetValue(afterField.FullName, out PdfFormField? beforeField))
            {
                violations.Add($"Form field '{afterField.FullName}' was added after DocMDP NoChanges certification.");
                continue;
            }

            if (!FieldValuesEqual(beforeField, afterField))
                violations.Add($"Form field '{afterField.FullName}' was modified after DocMDP NoChanges certification.");
        }
    }

    private static void CheckFieldMdp(
        string signatureFieldName,
        PdfFieldLock fieldLock,
        PdfDocument before,
        PdfDocument after,
        List<string> violations)
    {
        PdfForm? beforeForm = PdfForm.Open(before);
        PdfForm? afterForm = PdfForm.Open(after);
        if (beforeForm is null)
            return;

        Dictionary<string, PdfFormField> afterMap = afterForm?.ToDictionary(f => f.FullName, StringComparer.Ordinal) ?? [];

        foreach (PdfFormField beforeField in beforeForm)
        {
            if (beforeField.FullName == signatureFieldName || !fieldLock.IsFieldLocked(beforeField.FullName))
                continue;

            if (!afterMap.TryGetValue(beforeField.FullName, out PdfFormField? afterField))
            {
                violations.Add($"Locked form field '{beforeField.FullName}' was removed after signing.");
                continue;
            }

            if (!FieldValuesEqual(beforeField, afterField))
                violations.Add($"Locked form field '{beforeField.FullName}' was modified after signing.");
        }

        if (afterForm is null)
            return;

        Dictionary<string, PdfFormField> beforeMap = beforeForm.ToDictionary(f => f.FullName, StringComparer.Ordinal);
        foreach (PdfFormField afterField in afterForm)
        {
            if (afterField.FullName == signatureFieldName ||
                beforeMap.ContainsKey(afterField.FullName) ||
                IsDocumentTimestampField(afterField) ||
                !fieldLock.IsFieldLocked(afterField.FullName))
            {
                continue;
            }

            violations.Add($"Locked form field '{afterField.FullName}' was added after signing.");
        }
    }

    private static bool IsDocumentTimestampField(PdfFormField field) =>
        field.Type == PdfFieldType.Signature &&
        field.Inherited(PdfName.Get("V")).AsDictionary is { } sig &&
        (sig.GetName(PdfName.Get("SubFilter"))?.Value == "ETSI.RFC3161" ||
         sig.GetName(PdfName.Get("Type"))?.Value == "DocTimeStamp");

    private static bool FieldValuesEqual(PdfFormField left, PdfFormField right)
    {
        PdfValue leftVal = left.Inherited(PdfName.Get("V"));
        PdfValue rightVal = right.Inherited(PdfName.Get("V"));

        if (leftVal.Kind != rightVal.Kind)
            return false;

        return leftVal.Kind switch
        {
            PdfValueKind.Null => true,
            PdfValueKind.Boolean => leftVal.AsBoolean == rightVal.AsBoolean,
            PdfValueKind.Integer => leftVal.AsInteger == rightVal.AsInteger,
            PdfValueKind.Real => Math.Abs(leftVal.AsNumber - rightVal.AsNumber) < 1e-9,
            PdfValueKind.Name => leftVal.AsName == rightVal.AsName,
            PdfValueKind.String => leftVal.AsStringBytes.SequenceEqual(rightVal.AsStringBytes),
            PdfValueKind.Dictionary when left.Type == PdfFieldType.Signature =>
                SignatureContentsEqual(leftVal.AsDictionary!, rightVal.AsDictionary!),
            _ => leftVal.Equals(rightVal),
        };
    }

    private static bool SignatureContentsEqual(PdfDictionary left, PdfDictionary right) =>
        left.Get(PdfName.Get("Contents")).AsStringBytes.SequenceEqual(
            right.Get(PdfName.Get("Contents")).AsStringBytes);

    private static bool EndsWithEof(ReadOnlySpan<byte> prefix)
    {
        int end = prefix.Length;
        while (end > 0 && prefix[end - 1] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
            end--;

        return end >= 5 && prefix[..end].EndsWith("%%EOF"u8);
    }
}
