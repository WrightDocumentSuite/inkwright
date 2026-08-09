using System.Runtime.InteropServices;

namespace Inkwright.Fonts;

/// <summary>
/// Finds font files installed on the machine. The lookup indexes the family and PostScript names
/// stored inside each font in addition to its file name, then answers <see langword="null"/> when
/// no exact family or known legacy alias is available.
/// </summary>
public static class SystemFonts
{
    private static readonly Dictionary<string, string> FamilyAliases = new(StringComparer.Ordinal)
    {
        ["timesroman"] = "timesnewroman",
        ["timesnewromancyr"] = "timesnewroman",
        ["timesnewromance"] = "timesnewroman",
        ["timesnewromantur"] = "timesnewroman",
        ["arialcyr"] = "arial",
        ["arialce"] = "arial",
        ["arialtur"] = "arial",
        ["courier"] = "couriernew",
    };

    private static readonly Lazy<Dictionary<string, string>> Index =
        new(BuildIndex, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// The font files found on this machine, keyed by normalised family, PostScript, style and file
    /// names. A single file therefore normally appears under several keys.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Available => Index.Value;

    /// <summary>Locates a font file for a family and style.</summary>
    /// <param name="family">The family name, such as <c>Segoe UI</c>.</param>
    /// <param name="bold">Whether to prefer a bold face.</param>
    /// <param name="italic">Whether to prefer an italic face.</param>
    /// <returns>The path of the best match, or <see langword="null"/> when nothing matches.</returns>
    public static string? Find(string family, bool bold = false, bool italic = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(family);

        Dictionary<string, string> index = Index.Value;

        foreach (string key in FamilyKeys(family))
        {
            foreach (string candidate in Candidates(key, bold, italic))
            {
                if (index.TryGetValue(candidate, out string? path))
                    return path;
            }

            // Fall back to any face of the family, since a regular weight beats no font at all.
            foreach ((string name, string path) in index)
            {
                if (name.StartsWith(key, StringComparison.Ordinal))
                    return path;
            }
        }

        return null;
    }

    /// <summary>
    /// Lookup keys for a document-facing family. The name as written wins; a subset prefix,
    /// comma-style suffix and legacy Office/PDF alias are progressively weaker alternatives.
    /// </summary>
    internal static IEnumerable<string> FamilyKeys(string family)
    {
        string value = StripSubsetPrefix(family.Trim());
        string exact = Normalize(value);
        if (exact.Length == 0)
            yield break;

        yield return exact;

        int comma = value.IndexOf(',', StringComparison.Ordinal);
        string familyOnly = comma > 0 ? Normalize(value[..comma]) : exact;
        if (familyOnly != exact)
            yield return familyOnly;

        if (FamilyAliases.TryGetValue(familyOnly, out string? alias) && alias != familyOnly)
            yield return alias;
    }

    private static IEnumerable<string> Candidates(string key, bool bold, bool italic)
    {
        if (bold && italic)
        {
            yield return key + "boldital";
            yield return key + "bolditalic";
            yield return key + "bi";
            yield return key + "z";
        }

        if (bold)
        {
            yield return key + "bold";
            yield return key + "bd";
            yield return key + "b";
        }

        if (italic)
        {
            yield return key + "italic";
            yield return key + "ital";
            yield return key + "i";
        }

        yield return key;
        yield return key + "regular";
    }

    private static Dictionary<string, string> BuildIndex()
    {
        return BuildIndex(FontFiles());
    }

    /// <summary>Builds an index over an explicit set of files; separated for deterministic tests.</summary>
    internal static Dictionary<string, string> BuildIndex(IEnumerable<string> files)
    {
        var index = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (string file in files)
            IndexFont(index, file);

        return index;
    }

    private static IEnumerable<string> FontFiles()
    {
        foreach (string directory in Directories())
        {
            if (!Directory.Exists(directory))
                continue;

            string[] files;
            try
            {
                files = Directory.GetFiles(directory, "*", SearchOption.AllDirectories);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
            {
                continue;
            }

            foreach (string file in files)
            {
                string extension = Path.GetExtension(file);
                if (extension.Equals(".ttf", StringComparison.OrdinalIgnoreCase) ||
                    extension.Equals(".otf", StringComparison.OrdinalIgnoreCase) ||
                    extension.Equals(".ttc", StringComparison.OrdinalIgnoreCase))
                {
                    yield return file;
                }
            }
        }
    }

    private static void IndexFont(Dictionary<string, string> index, string file)
    {
        string fileName = Normalize(Path.GetFileNameWithoutExtension(file));

        try
        {
            TrueTypeFont font = TrueTypeFont.Load(file);
            string family = Normalize(font.FamilyName);
            string postScript = Normalize(font.PostScriptName);
            bool bold = font.Weight >= 600 || HasStyleWord(postScript, "bold", "black", "heavy", "semibold");
            bool italic = Math.Abs(font.ItalicAngle) > 0.01 || HasStyleWord(postScript, "italic", "oblique");

            if (family.Length > 0 && family != "untitled")
            {
                foreach (string key in FaceKeys(family, bold, italic))
                    index.TryAdd(key, file);
            }

            if (postScript.Length > 0 && postScript != "untitled")
                index.TryAdd(postScript, file);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           InvalidDataException or ArgumentException or OverflowException)
        {
            // A broken or unsupported face must not hide the rest of the machine's fonts. Its file
            // name remains useful for callers that deliberately know it.
        }

        if (fileName.Length > 0)
            index.TryAdd(fileName, file);
    }

    private static IEnumerable<string> FaceKeys(string family, bool bold, bool italic)
    {
        if (bold && italic)
        {
            yield return family + "bolditalic";
            yield return family + "boldital";
            yield return family + "bi";
            yield return family + "z";
            yield break;
        }

        if (bold)
        {
            yield return family + "bold";
            yield return family + "bd";
            yield return family + "b";
            yield break;
        }

        if (italic)
        {
            yield return family + "italic";
            yield return family + "ital";
            yield return family + "i";
            yield break;
        }

        yield return family;
        yield return family + "regular";
    }

    private static IEnumerable<string> Directories()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts");
            yield return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "Windows", "Fonts");
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            yield return "/System/Library/Fonts";
            yield return "/Library/Fonts";
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library/Fonts");
        }
        else
        {
            yield return "/usr/share/fonts";
            yield return "/usr/local/share/fonts";
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".fonts");
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local/share/fonts");
        }
    }

    private static string Normalize(string name)
    {
        Span<char> buffer = name.Length <= 128 ? stackalloc char[name.Length] : new char[name.Length];
        int length = 0;

        foreach (char c in name)
        {
            if (char.IsAsciiLetterOrDigit(c))
                buffer[length++] = char.ToLowerInvariant(c);
        }

        return new string(buffer[..length]);
    }

    private static string StripSubsetPrefix(string name)
    {
        if (name.Length <= 7 || name[6] != '+')
            return name;

        for (int i = 0; i < 6; i++)
        {
            if (name[i] is < 'A' or > 'Z')
                return name;
        }

        return name[7..];
    }

    private static bool HasStyleWord(string value, params string[] words) =>
        words.Any(word => value.Contains(word, StringComparison.Ordinal));
}
