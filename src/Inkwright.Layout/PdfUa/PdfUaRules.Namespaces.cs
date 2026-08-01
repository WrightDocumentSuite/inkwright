using Inkwright.Cos;

namespace Inkwright.Layout;

/// <summary>
/// The structure namespaces of PDF 2.0, as ISO 32000-2 §14.8.6 defines them and ISO/TS 32005
/// elaborates. A namespace is what lets two tag sets use the same tag name for different things
/// without either having to give the name up, and PDF/UA-2 leans on it.
/// </summary>
internal static partial class PdfUaRules
{
    /// <summary>The standard structure namespace for PDF 2.0.</summary>
    internal const string Pdf20Namespace = "http://iso.org/pdf2/ssn";

    /// <summary>
    /// The standard structure namespace for PDF 1.7, which is also the default: an element that
    /// names no namespace is taken to be in this one.
    /// </summary>
    internal const string Pdf17Namespace = "http://iso.org/pdf/ssn";

    /// <summary>The only domain-specific namespace PDF 2.0 defines, for mathematics.</summary>
    internal const string MathMlNamespace = "http://www.w3.org/1998/Math/MathML";

    /// <summary>
    /// Checks the namespaces a document's structure uses (ISO 32000-2 §14.8.6, ISO/TS 32005). Every
    /// element has to be in one of the standard namespaces or in one that role-maps into them, and
    /// a namespace an element points at has to be declared where a reader will look for it.
    /// </summary>
    internal static void CheckNamespaces(PdfUaContext context)
    {
        if (context.Resolve(context.Catalog.Get(PdfName.Get("StructTreeRoot"))).AsDictionary is not { } root)
            return;

        Dictionary<PdfDictionary, string> declared = ReadDeclared(context, root);
        bool anyNamespaced = false;

        foreach (Tagging.PdfStructureNode node in context.AllNodes())
        {
            PdfValue entry = node.Dictionary.Get(PdfName.Get("NS"));
            if (entry.IsNull)
                continue;

            anyNamespaced = true;

            if (context.Resolve(entry).AsDictionary is not { } dictionary)
            {
                context.Violation("8.2.2", $"A {node.Tag} element points at something that is not a namespace.");
                continue;
            }

            if (!declared.TryGetValue(dictionary, out string? name))
            {
                context.Violation(
                    "8.2.2",
                    $"A {node.Tag} element uses a namespace the structure tree root does not declare.");
                continue;
            }

            if (name is Pdf20Namespace or Pdf17Namespace or MathMlNamespace)
                continue;

            // A namespace of one's own has to say how its tags map onto the standard ones.
            if (context.Resolve(dictionary.Get(PdfName.Get("RoleMapNS"))).AsDictionary is null)
            {
                context.Violation(
                    "8.2.2",
                    $"The namespace {name} defines no role map, so its tags cannot be understood.");
            }
        }

        if (!anyNamespaced && declared.Count == 0)
        {
            // The default namespace is the PDF 1.7 one, which is legal but loses everything PDF 2.0
            // added to the tag set.
            context.Advice(
                "8.2.2",
                "No structure element names a namespace, so all of them fall into the PDF 1.7 default.");
        }
    }

    private static Dictionary<PdfDictionary, string> ReadDeclared(PdfUaContext context, PdfDictionary root)
    {
        var declared = new Dictionary<PdfDictionary, string>(ReferenceEqualityComparer.Instance
            as IEqualityComparer<PdfDictionary> ?? EqualityComparer<PdfDictionary>.Default);

        if (context.Resolve(root.Get(PdfName.Get("Namespaces"))).AsArray is not { } namespaces)
            return declared;

        for (int i = 0; i < namespaces.Count; i++)
        {
            if (context.Resolve(namespaces[i]).AsDictionary is not { } dictionary)
                continue;

            string? name = dictionary.GetTextString(PdfName.Get("NS"));

            if (string.IsNullOrWhiteSpace(name))
            {
                context.Violation("8.2.2", "A declared namespace states no name.");
                continue;
            }

            declared[dictionary] = name;
        }

        return declared;
    }
}
