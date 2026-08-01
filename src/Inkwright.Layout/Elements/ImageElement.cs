using Inkwright.Images;

namespace Inkwright.Layout.Elements;

/// <summary>How an image is fitted into the space it is given.</summary>
public enum ImageFit
{
    /// <summary>Fill the width and take whatever height the aspect ratio implies.</summary>
    Width,

    /// <summary>Fit entirely inside the space, leaving empty room on one axis.</summary>
    Contain,

    /// <summary>Stretch to the exact box, ignoring the aspect ratio.</summary>
    Fill,
}

/// <summary>Draws an image, sized by its aspect ratio unless told otherwise.</summary>
public sealed class ImageElement : LayoutElement
{
    /// <summary>The image to draw.</summary>
    public required PdfImage Image { get; init; }

    /// <summary>How the image is fitted into the space.</summary>
    public ImageFit Fit { get; set; } = ImageFit.Width;

    /// <summary>The width to use, overriding the fit.</summary>
    public double? Width { get; set; }

    /// <summary>The height to use, overriding the fit.</summary>
    public double? Height { get; set; }

    /// <summary>Text describing the image for a reader that cannot see it.</summary>
    public string? AlternateText { get; set; }

    /// <inheritdoc />
    public override SpacePlan Measure(LayoutSize available, LayoutContext context)
    {
        LayoutSize size = Resolve(available);

        if (size.Width <= 0 || size.Height <= 0)
            return SpacePlan.Empty();

        return size.Height > available.Height + 0.01
            ? SpacePlan.Wrap($"the image needs {size.Height:0.##} pt of height")
            : SpacePlan.Full(size.Width, size.Height);
    }

    /// <inheritdoc />
    public override void Draw(LayoutSize available, LayoutContext context)
    {
        if (context.Canvas is not { } canvas)
            return;

        using IDisposable tag = context.MarkedOrNothing("Figure", AlternateText);
        LayoutSize size = Resolve(available);
        canvas.DrawImage(Image, 0, 0, size.Width, size.Height);
    }

    private LayoutSize Resolve(LayoutSize available)
    {
        double ratio = Image.Height <= 0 ? 1 : (double)Image.Width / Image.Height;

        if (Width is { } width && Height is { } height)
            return new LayoutSize(width, height);

        if (Width is { } onlyWidth)
            return new LayoutSize(onlyWidth, onlyWidth / ratio);

        if (Height is { } onlyHeight)
            return new LayoutSize(onlyHeight * ratio, onlyHeight);

        return Fit switch
        {
            ImageFit.Fill => available,
            ImageFit.Contain => available.Width / ratio <= available.Height
                ? new LayoutSize(available.Width, available.Width / ratio)
                : new LayoutSize(available.Height * ratio, available.Height),
            _ => new LayoutSize(available.Width, available.Width / ratio),
        };
    }
}
