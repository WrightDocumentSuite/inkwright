namespace Inkwright.Metadata;

/// <summary>One property of a schema that PDF/A has never heard of.</summary>
/// <param name="Name">The property name, without a prefix.</param>
/// <param name="ValueType">An XMP value type, such as <c>Text</c> or <c>seq Text</c>.</param>
/// <param name="Category">Either <c>internal</c> or <c>external</c>.</param>
/// <param name="Description">What the property means, for a reader who has only this file.</param>
public sealed record XmpSchemaProperty(string Name, string ValueType, string Category, string Description);

/// <summary>
/// A description of a metadata schema of one's own, in the form PDF/A requires: a name, the
/// namespace it lives in, the prefix it prefers, and an account of every property it defines.
/// </summary>
/// <param name="Name">A human-readable name for the schema.</param>
/// <param name="NamespaceUri">The namespace URI the properties belong to.</param>
/// <param name="Prefix">The preferred namespace prefix.</param>
/// <param name="Properties">Every property the schema defines.</param>
public sealed record XmpExtensionSchema(
    string Name,
    string NamespaceUri,
    string Prefix,
    IReadOnlyList<XmpSchemaProperty> Properties);
