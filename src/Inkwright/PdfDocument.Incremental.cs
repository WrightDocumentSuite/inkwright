using Inkwright.Cos;
using Inkwright.IO;

namespace Inkwright;

public sealed partial class PdfDocument
{
    /// <summary>
    /// Appends the changes to the original file instead of rewriting it. The bytes that were there
    /// stay byte for byte where they were, which is the only way to change a signed document without
    /// invalidating the signatures that came before.
    /// </summary>
    /// <param name="stream">Where to write the whole file: original bytes first, then the update.</param>
    /// <param name="options">How to lay the appended section out; pruning and encryption do not apply.</param>
    /// <exception cref="InvalidOperationException">
    /// The document was created from scratch, or its cross-reference data had to be rebuilt, in
    /// which case an appended section would chain back to something that was never trustworthy.
    /// </exception>
    public void SaveIncremental(Stream stream, PdfSaveOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_reader is null)
            throw new InvalidOperationException("An incremental save needs an original file to append to.");

        if (_reader.WasRebuilt || _reader.StartXref < 0)
        {
            throw new InvalidOperationException(
                "The source file's cross-reference data was rebuilt, so it cannot be extended; save a full copy instead.");
        }

        options ??= PdfSaveOptions.Default;
        PrepareForSave(options);

        ReadOnlyMemory<byte> original = _reader.FileBytes;
        stream.Write(original.Span);

        long baseLength = original.Length;
        if (original.Length > 0 && original.Span[^1] != (byte)'\n')
        {
            stream.WriteByte((byte)'\n');
            baseLength++;
        }

        PdfVersion version = PdfVersion.Max(Version, _reader.Version);

        // An appended section is still part of the same file, and a file that declares 1.4 has no
        // business acquiring object streams or a cross-reference stream in its update: that is how
        // an archival PDF/A-1 document quietly stops being one.
        var writeOptions = new PdfWriteOptions
        {
            UseObjectStreams = options.UseObjectStreams && _reader.Security is null && version >= PdfVersion.Pdf15,
            Compress = options.Compress,
        };

        using PdfWriter writer = PdfWriter.Append(stream, version, baseLength, _reader.StartXref, writeOptions);

        writer.ReserveThrough(Math.Max(_reader.MaxObjectNumber, _nextNumber - 1));
        writer.UseEncryption(_reader.Security);

        foreach (int number in ChangedObjectNumbers())
        {
            PdfValue value = GetObject(number);
            if (value.IsNull)
                continue;

            writer.WriteObject(new PdfObjectId(number, GenerationOf(number)), value);
        }

        writer.Finish(BuildIncrementalTrailer());
    }

    /// <summary>Appends the changes to a copy of the original file on disk.</summary>
    /// <param name="path">Where to write the updated file.</param>
    /// <param name="options">How to lay the appended section out.</param>
    public void SaveIncremental(string path, PdfSaveOptions? options = null)
    {
        using FileStream stream = File.Create(path);
        SaveIncremental(stream, options);
    }

    /// <summary>Appends the changes and returns the whole updated file.</summary>
    /// <param name="options">How to lay the appended section out.</param>
    public byte[] ToIncrementalArray(PdfSaveOptions? options = null)
    {
        using var buffer = new MemoryStream((_reader?.FileBytes.Length ?? 0) + 8192);
        SaveIncremental(buffer, options);
        return buffer.ToArray();
    }

    /// <summary>The objects an incremental save would write.</summary>
    internal SortedSet<int> ChangedObjectNumbers()
    {
        SortedSet<int> numbers = [.. _changed];
        foreach (int number in _added.Keys)
            numbers.Add(number);

        return numbers;
    }

    private PdfDictionary BuildIncrementalTrailer()
    {
        var trailer = new PdfDictionary(5);
        trailer.Set(PdfNames.Root, PdfValue.Reference(CatalogId));

        if (InfoId.IsValid)
            trailer.Set(PdfNames.Info, PdfValue.Reference(InfoId));

        if (Trailer.TryGetRaw(PdfNames.Encrypt, out PdfValue encrypt) && !encrypt.IsNull)
            trailer.Set(PdfNames.Encrypt, encrypt);

        // The first half of the identifier is permanent; the second half changes with every update.
        byte[] permanent = Trailer.GetArray(PdfNames.ID) is { Count: > 0 } existing
            ? existing.Get(0).AsStringBytes.ToArray()
            : System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);

        trailer.Set(PdfNames.ID, PdfValue.Array(new PdfArray(
            PdfValue.String(permanent, hex: true),
            PdfValue.String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16), hex: true))));

        // Integrity protection is declared in the trailer, and an appended revision has to carry
        // the declaration forward or the code it adds would have nothing pointing at it.
        if (Trailer.TryGetRaw(PdfName.Get("AuthCode"), out PdfValue authentication) && !authentication.IsNull)
            trailer.Set(PdfName.Get("AuthCode"), authentication);

        return trailer;
    }
}
