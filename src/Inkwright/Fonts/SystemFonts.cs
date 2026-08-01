using System.Runtime.InteropServices;

namespace Inkwright.Fonts;

/// <summary>
/// Finds font files installed on the machine. The lookup is deliberately simple and explicit: it
/// matches file names in the platform's font directories rather than pretending to be a font
/// configuration system, and it answers <see langword="null"/> when it is not sure.
/// </summary>
public static class SystemFonts
{
    private static readonly Lazy<Dictionary<string, string>> Index =
        new(BuildIndex, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The font files found on this machine, keyed by a normalised file name.</summary>
    public static IReadOnlyDictionary<string, string> Available => Index.Value;

    /// <summary>Locates a font file for a family and style.</summary>
    /// <param name="family">The family name, such as <c>Segoe UI</c>.</param>
    /// <param name="bold">Whether to prefer a bold face.</param>
    /// <param name="italic">Whether to prefer an italic face.</param>
    /// <returns>The path of the best match, or <see langword="null"/> when nothing matches.</returns>
    public static string? Find(string family, bool bold = false, bool italic = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(family);

        string key = Normalize(family);
        Dictionary<string, string> index = Index.Value;

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

        return null;
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
        var index = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (string directory in Directories())
        {
            if (!Directory.Exists(directory))
                continue;

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories);
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (string file in files)
            {
                string extension = Path.GetExtension(file);
                if (!extension.Equals(".ttf", StringComparison.OrdinalIgnoreCase) &&
                    !extension.Equals(".otf", StringComparison.OrdinalIgnoreCase) &&
                    !extension.Equals(".ttc", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                index.TryAdd(Normalize(Path.GetFileNameWithoutExtension(file)), file);
            }
        }

        return index;
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
}
