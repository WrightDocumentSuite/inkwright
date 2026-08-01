using Inkwright.Cos;

namespace Inkwright.Layout;

/// <summary>
/// The rule ISO 14289-2 §8.8 states about where a link may land: inside the document a destination
/// has to name a structure element, not a page. A page number survives only until the content is
/// reflowed, which is exactly what accessible reading does to it; a structure element survives.
/// </summary>
internal static partial class PdfUaRules
{
    /// <summary>How far a chain of <c>Next</c> actions is followed before it is taken to be looping.</summary>
    private const int MaxActionChain = 32;

    /// <summary>
    /// Checks every destination the document can reach: link and widget annotations, outline
    /// entries, the action the document opens with, and the named destinations themselves. Part 1
    /// has no equivalent requirement, because PDF 1.7 has no structure destinations to demand.
    /// </summary>
    internal static void CheckDestinations(PdfUaContext context)
    {
        foreach ((PdfPage _, PdfDictionary annotation) in context.Annotations())
        {
            CheckDestination(context, annotation.Get(PdfName.Get("Dest")));
            CheckAction(context, annotation.Get(PdfName.Get("A")));
        }

        foreach (PdfDictionary item in Outline(context))
        {
            CheckDestination(context, item.Get(PdfName.Get("Dest")));
            CheckAction(context, item.Get(PdfName.Get("A")));
        }

        PdfValue open = context.Catalog.Get(PdfName.Get("OpenAction"));
        if (context.Resolve(open).AsArray is not null)
            CheckDestination(context, open);
        else
            CheckAction(context, open);

        // A named destination is a destination in its own right, and one that no link happens to
        // use today is still there for the next tool that writes one.
        foreach ((string _, PdfValue value) in PdfNameTree.Enumerate(NamedDestinations(context)))
            CheckDestination(context, value);

        if (context.Resolve(context.Catalog.Get(PdfName.Get("Dests"))).AsDictionary is { } legacy)
        {
            foreach (PdfName key in legacy.Keys)
                CheckDestination(context, legacy.Get(key));
        }
    }

    /// <summary>
    /// Follows an action to the destination it carries. Only <c>GoTo</c> stays inside the document;
    /// <c>GoToR</c> and <c>GoToE</c> land in another file, which §8.8 does not speak about.
    /// </summary>
    private static void CheckAction(PdfUaContext context, PdfValue value)
    {
        for (int depth = 0; depth < MaxActionChain; depth++)
        {
            if (context.Resolve(value).AsDictionary is not { } action)
                return;

            if (context.Resolve(action.Get(PdfName.Get("S"))).AsName?.Value == "GoTo")
                CheckDestination(context, action.Get(PdfName.Get("D")));

            value = action.Get(PdfName.Get("Next"));
        }
    }

    /// <summary>
    /// Judges one destination. A name or a string stands for an entry in the name tree and is
    /// followed there first; anything that cannot be resolved to an explicit array is left alone,
    /// because a rule that guesses reports violations against files that have none.
    /// </summary>
    private static void CheckDestination(PdfUaContext context, PdfValue value)
    {
        if (Explicit(context, value) is not { Count: > 0 } destination)
            return;

        if (IsStructureElement(context, destination.Get(0)))
            return;

        if (!NamesAPage(context, destination.Get(0)))
            return;

        context.Violation(
            "8.8",
            "A destination inside the document names a page rather than a structure element.");
    }

    /// <summary>Resolves a destination to the explicit array of §12.3.2.2, following one name.</summary>
    private static PdfArray? Explicit(PdfUaContext context, PdfValue value)
    {
        PdfValue resolved = context.Resolve(value);

        if (resolved.AsArray is { } array)
            return array;

        string? key = resolved.AsName?.Value ?? resolved.AsTextString;
        if (key is null)
            return null;

        PdfValue found = PdfNameTree.Find(NamedDestinations(context), key);

        if (found.IsNull && context.Resolve(context.Catalog.Get(PdfName.Get("Dests"))).AsDictionary is { } legacy)
            found = legacy.Get(PdfName.Get(key));

        PdfValue target = context.Resolve(found);

        // The name may stand for a bare array or for a dictionary that holds one under D.
        return target.AsArray ?? (target.AsDictionary is { } wrapper
            ? context.Resolve(wrapper.Get(PdfName.Get("D"))).AsArray
            : null);
    }

    private static PdfDictionary? NamedDestinations(PdfUaContext context) =>
        context.Resolve(
            context.Resolve(context.Catalog.Get(PdfName.Get("Names"))).AsDictionary?.Get(PdfName.Get("Dests"))
            ?? PdfValue.Null).AsDictionary;

    /// <summary>
    /// Whether the target is a structure element. <c>Type</c> is optional on one (§14.7.2), so the
    /// structure type under <c>S</c> is what settles it when the type is left out.
    /// </summary>
    private static bool IsStructureElement(PdfUaContext context, PdfValue target)
    {
        if (context.Resolve(target).AsDictionary is not { } dictionary)
            return false;

        string? type = context.Resolve(dictionary.Get(PdfNames.Type)).AsName?.Value;
        return type == "StructElem" ||
               (type is null && context.Resolve(dictionary.Get(PdfName.Get("S"))).AsName is not null);
    }

    /// <summary>
    /// Whether the target names a page: either a page object or, in the form a named destination
    /// may take, the index of one. Anything else is not judged.
    /// </summary>
    private static bool NamesAPage(PdfUaContext context, PdfValue target)
    {
        PdfValue resolved = context.Resolve(target);

        if (resolved.Kind == PdfValueKind.Integer)
            return true;

        return resolved.AsDictionary is { } dictionary &&
               context.Resolve(dictionary.Get(PdfNames.Type)).AsName?.Value == "Page";
    }

    /// <summary>Walks the outline, which is a linked tree of <c>First</c> children and <c>Next</c> siblings.</summary>
    private static IEnumerable<PdfDictionary> Outline(PdfUaContext context)
    {
        if (context.Resolve(context.Catalog.Get(PdfName.Get("Outlines"))).AsDictionary is not { } root)
            yield break;

        var seen = new HashSet<PdfDictionary>();
        var pending = new Stack<PdfDictionary>();

        if (context.Resolve(root.Get(PdfName.Get("First"))).AsDictionary is { } first)
            pending.Push(first);

        while (pending.Count > 0)
        {
            PdfDictionary item = pending.Pop();
            if (!seen.Add(item))
                continue;

            yield return item;

            if (context.Resolve(item.Get(PdfName.Get("Next"))).AsDictionary is { } next)
                pending.Push(next);

            if (context.Resolve(item.Get(PdfName.Get("First"))).AsDictionary is { } child)
                pending.Push(child);
        }
    }
}
