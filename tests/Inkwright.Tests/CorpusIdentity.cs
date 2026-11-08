using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Inkwright.Tests;

internal readonly record struct CorpusFile(string LogicalPath, string PhysicalPath);

internal readonly record struct CorpusIdentityResult(int FileCount, long TotalBytes, string Sha256);

/// <summary>Computes the content-addressed identity of a corpus without retaining a file manifest.</summary>
internal static class CorpusIdentity
{
    private const string Domain = "inkwright-reference-corpus-v1\n";
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

    internal static CorpusIdentityResult Compute(IEnumerable<CorpusFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        CorpusFile[] sorted = files
            .Select(file => new CorpusFile(NormalizeLogicalPath(file.LogicalPath), file.PhysicalPath))
            .OrderBy(file => file.LogicalPath, StringComparer.Ordinal)
            .ToArray();

        using var treeHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        treeHash.AppendData(Utf8.GetBytes(Domain));

        long totalBytes = 0;
        string? previousPath = null;
        foreach (CorpusFile file in sorted)
        {
            if (string.Equals(file.LogicalPath, previousPath, StringComparison.Ordinal))
                throw new InvalidOperationException($"Duplicate normalized corpus path '{file.LogicalPath}'.");

            previousPath = file.LogicalPath;
            using FileStream stream = File.OpenRead(file.PhysicalPath);
            long length = stream.Length;
            string fileHash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            totalBytes = checked(totalBytes + length);

            string record = file.LogicalPath + '\0' +
                length.ToString(CultureInfo.InvariantCulture) + '\0' + fileHash + '\n';
            treeHash.AppendData(Utf8.GetBytes(record));
        }

        string digest = Convert.ToHexString(treeHash.GetHashAndReset()).ToLowerInvariant();
        return new CorpusIdentityResult(sorted.Length, totalBytes, digest);
    }

    private static string NormalizeLogicalPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return path.Replace('\\', '/').Normalize(NormalizationForm.FormC);
    }
}
