namespace Inkwright.Cos;

/// <summary>
/// Turns indirect references into the objects they point at. Containers read from a file carry a
/// resolver so that navigating an object graph never silently stops at a reference.
/// </summary>
public interface IPdfResolver
{
    /// <summary>
    /// Follows <paramref name="value"/> while it is an indirect reference and returns the object
    /// it designates. Broken or cyclic references resolve to the null object.
    /// </summary>
    /// <param name="value">The value to resolve; non-references are returned unchanged.</param>
    PdfValue Resolve(PdfValue value);
}
