using System.Collections.Concurrent;

namespace Inkwright.Fonts;

/// <summary>
/// Supplies the predefined CMaps that ISO 32000-1 Table 118 names but does not print: the CJK
/// encodings and the character-collection maps that turn CIDs into text. The tables are large
/// enough that the core library does not carry them, so a caller who reads CJK documents registers
/// a source such as the one in the <c>Inkwright.Cjk</c> package.
/// </summary>
public interface ICMapSource
{
    /// <summary>Loads a predefined CMap.</summary>
    /// <param name="name">The CMap name, such as <c>90ms-RKSJ-H</c> or <c>Adobe-Japan1-UCS2</c>.</param>
    /// <returns>The map, or <see langword="null"/> when this source does not know the name.</returns>
    public CMap? Load(string name);
}

/// <summary>
/// The registry the library asks for predefined CMaps. Lookups are memoised, so a source may load
/// its tables the slow way; it is asked for a given name at most once.
/// </summary>
public static class CMapProvider
{
    private static readonly ConcurrentDictionary<string, CMap?> Cache = new(StringComparer.Ordinal);
    private static ICMapSource? _source;

    /// <summary>
    /// The source predefined CMaps come from. Setting it clears whatever the previous source had
    /// already answered.
    /// </summary>
    public static ICMapSource? Source
    {
        get => _source;
        set
        {
            _source = value;
            Cache.Clear();
        }
    }

    /// <summary>Looks up a predefined CMap through the registered source.</summary>
    /// <param name="name">The CMap name.</param>
    /// <returns>The map, or <see langword="null"/> when no source knows the name.</returns>
    public static CMap? Load(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        if (_source is null)
            return null;

        return Cache.GetOrAdd(name, static (key, source) => Read(source, key), _source);
    }

    private static CMap? Read(ICMapSource source, string name)
    {
        try
        {
            return source.Load(name);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or FormatException)
        {
            // A damaged table is worth no more than an unknown name: extraction falls back to the
            // font's own ToUnicode map rather than failing the whole document.
            return null;
        }
    }
}
