namespace Inkwright.IO;

/// <summary>Which of the three cross-reference entry shapes of ISO 32000-1 §7.5.8.3 an entry is.</summary>
internal enum XrefEntryType : byte
{
    /// <summary>A free entry: the object number is unused.</summary>
    Free = 0,

    /// <summary>An object stored at a byte offset in the file.</summary>
    Normal = 1,

    /// <summary>An object stored inside an object stream.</summary>
    Compressed = 2,
}

/// <summary>Where an object lives, as recorded by a cross-reference table or stream.</summary>
/// <param name="Type">The entry shape.</param>
/// <param name="Offset">The byte offset for a normal entry, or the object stream number for a compressed one.</param>
/// <param name="Generation">The generation for a normal entry, or the index within the object stream.</param>
internal readonly record struct XrefEntry(XrefEntryType Type, long Offset, int Generation)
{
    internal static XrefEntry Normal(long offset, int generation) => new(XrefEntryType.Normal, offset, generation);

    internal static XrefEntry Compressed(int streamNumber, int index) =>
        new(XrefEntryType.Compressed, streamNumber, index);

    internal static XrefEntry Free => new(XrefEntryType.Free, 0, 0);
}
