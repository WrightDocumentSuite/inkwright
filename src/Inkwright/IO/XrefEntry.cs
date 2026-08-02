namespace Inkwright.IO;

/// <summary>How a cross-reference row resolves under ISO 32000-2 §7.5.8.3.</summary>
internal enum XrefEntryType : byte
{
    /// <summary>A free entry: the object number is unused.</summary>
    Free = 0,

    /// <summary>An object stored at a byte offset in the file.</summary>
    Normal = 1,

    /// <summary>An object stored inside an object stream.</summary>
    Compressed = 2,

    /// <summary>A future xref-stream entry type, which resolves to null but is not a free-list row.</summary>
    Unknown = 3,
}

/// <summary>Where an object lives, as recorded by a cross-reference table or stream.</summary>
/// <param name="Type">The entry shape.</param>
/// <param name="Offset">
/// The byte offset for a normal entry, object stream number for a compressed entry, or next free
/// object number for a free entry.
/// </param>
/// <param name="Generation">
/// The object generation for normal/free entries, or the slot index for a compressed entry.
/// </param>
internal readonly record struct XrefEntry(XrefEntryType Type, long Offset, int Generation)
{
    internal static XrefEntry Normal(long offset, int generation) => new(XrefEntryType.Normal, offset, generation);

    internal static XrefEntry Compressed(int streamNumber, int index) =>
        new(XrefEntryType.Compressed, streamNumber, index);

    internal static XrefEntry Free(long nextFree, int generation) =>
        new(XrefEntryType.Free, nextFree, generation);

    internal static XrefEntry Unknown => new(XrefEntryType.Unknown, 0, 0);
}
