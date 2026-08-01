using System.Reflection;
using Inkwright.Fonts;

namespace Inkwright.Cjk;

/// <summary>
/// The predefined CMaps of ISO 32000 Table 118, carried as resources inside this assembly. Register
/// them once at start-up and every composite font that names one of Adobe's CJK encodings reads
/// correctly, including the documents that carry no <c>ToUnicode</c> map and rely on the character
/// collection to say what their CIDs mean.
/// </summary>
/// <example>
/// <code>
/// CjkCMaps.Register();
/// using var document = PdfDocument.Load("japanese.pdf");
/// string text = document.Pages[0].ExtractText();
/// </code>
/// </example>
public sealed class CjkCMaps : ICMapSource
{
    /// <summary>How far a chain of <c>usecmap</c> references may run before it is called a cycle.</summary>
    private const int MaxDepth = 8;

    private static readonly Assembly Assembly = typeof(CjkCMaps).Assembly;
    private static readonly Lazy<HashSet<string>> KnownNames = new(ReadNames, LazyThreadSafetyMode.ExecutionAndPublication);

    [ThreadStatic]
    private static int _depth;

    /// <summary>The shared instance; the maps it reads are cached by <see cref="CMapProvider"/>.</summary>
    public static CjkCMaps Instance { get; } = new();

    /// <summary>The names this assembly carries, such as <c>90ms-RKSJ-H</c> and <c>Adobe-Japan1-UCS2</c>.</summary>
    public static IReadOnlyCollection<string> Names => KnownNames.Value;

    /// <summary>Makes these maps the source Inkwright asks for predefined CMaps.</summary>
    public static void Register() => CMapProvider.Source = Instance;

    /// <inheritdoc />
    public CMap? Load(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        if (!KnownNames.Value.Contains(name))
            return null;

        if (_depth >= MaxDepth)
            return null;

        using Stream? stream = Assembly.GetManifestResourceStream(ResourceName(name));
        if (stream is null)
            return null;

        byte[] data = new byte[(int)stream.Length];
        stream.ReadExactly(data);

        CMap map = CMap.CreateEmpty();
        string? parent = BinaryCMapReader.Read(data, map);

        if (parent is null)
            return map;

        _depth++;
        try
        {
            // The parent goes through the provider so that a chain shared by several maps is read
            // once, and so that a caller who overrode one name sees the override honoured here too.
            if (CMapProvider.Load(parent) is { } inherited)
                map.IncludeMap(inherited);
        }
        finally
        {
            _depth--;
        }

        return map;
    }

    private static string ResourceName(string name) => $"Inkwright.Cjk.cmaps.{name}.bcmap";

    private static HashSet<string> ReadNames()
    {
        const string Prefix = "Inkwright.Cjk.cmaps.";
        const string Suffix = ".bcmap";

        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (string resource in Assembly.GetManifestResourceNames())
        {
            if (resource.StartsWith(Prefix, StringComparison.Ordinal) &&
                resource.EndsWith(Suffix, StringComparison.Ordinal))
            {
                names.Add(resource[Prefix.Length..^Suffix.Length]);
            }
        }

        return names;
    }
}
