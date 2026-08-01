using System.Globalization;

namespace Inkwright.Layout;

/// <summary>What an element decided it can do with the space it was offered.</summary>
public enum SpacePlanKind : byte
{
    /// <summary>The element has nothing to draw and takes no space.</summary>
    Empty,

    /// <summary>The element cannot fit at all here and must be tried on the next page.</summary>
    Wrap,

    /// <summary>The element drew part of itself and has more left for the next page.</summary>
    Partial,

    /// <summary>The element drew all of itself.</summary>
    Full,
}

/// <summary>
/// The answer an element gives when asked how it would use a piece of space. Measuring returns a
/// plan rather than a size so that "this does not fit, but half of it would" is expressible, which
/// is what makes content flow across pages without a second layout model.
/// </summary>
/// <param name="Kind">What the element can do with the space.</param>
/// <param name="Width">The width it would occupy.</param>
/// <param name="Height">The height it would occupy.</param>
/// <param name="Reason">Why it wrapped, for the diagnostics an overflow produces.</param>
public readonly record struct SpacePlan(SpacePlanKind Kind, double Width, double Height, string? Reason = null)
{
    /// <summary>Nothing to draw.</summary>
    public static SpacePlan Empty() => new(SpacePlanKind.Empty, 0, 0);

    /// <summary>The element does not fit here.</summary>
    /// <param name="reason">What made it not fit.</param>
    public static SpacePlan Wrap(string reason) => new(SpacePlanKind.Wrap, 0, 0, reason);

    /// <summary>The element fits entirely.</summary>
    /// <param name="width">The width it occupies.</param>
    /// <param name="height">The height it occupies.</param>
    public static SpacePlan Full(double width, double height) => new(SpacePlanKind.Full, width, height);

    /// <summary>The element fits partly and has more to draw.</summary>
    /// <param name="width">The width the drawn part occupies.</param>
    /// <param name="height">The height the drawn part occupies.</param>
    public static SpacePlan Partial(double width, double height) => new(SpacePlanKind.Partial, width, height);

    /// <summary>Whether the element drew anything at all.</summary>
    public bool DrawsSomething => Kind is SpacePlanKind.Partial or SpacePlanKind.Full;

    /// <summary>The size the plan occupies.</summary>
    public LayoutSize Size => new(Width, Height);

    /// <inheritdoc />
    public override string ToString() => Kind == SpacePlanKind.Wrap
        ? $"Wrap ({Reason})"
        : string.Create(CultureInfo.InvariantCulture, $"{Kind} {Width:0.##} x {Height:0.##}");
}

/// <summary>A width and a height in points.</summary>
/// <param name="Width">The horizontal extent.</param>
/// <param name="Height">The vertical extent.</param>
public readonly record struct LayoutSize(double Width, double Height)
{
    /// <summary>A size with no extent.</summary>
    public static LayoutSize Zero => default;

    /// <summary>Whether either extent is zero or less.</summary>
    public bool IsEmpty => Width <= 0 || Height <= 0;

    /// <summary>Shrinks the size, clamping at zero.</summary>
    /// <param name="horizontal">How much to take off the width.</param>
    /// <param name="vertical">How much to take off the height.</param>
    public LayoutSize Shrink(double horizontal, double vertical) =>
        new(Math.Max(0, Width - horizontal), Math.Max(0, Height - vertical));

    /// <inheritdoc />
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Width:0.##} x {Height:0.##}");
}
