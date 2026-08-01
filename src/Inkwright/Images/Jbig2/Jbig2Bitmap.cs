namespace Inkwright.Images.Jbig2;

/// <summary>
/// A bi-level bitmap, one byte per pixel while it is being built. Bits would be more compact, but
/// the generic region procedure reads neighbours at arbitrary offsets on three rows at once, and
/// bytes make that a plain index rather than a shift and a mask.
/// </summary>
internal sealed class Jbig2Bitmap
{
    private readonly byte[] _pixels;

    internal Jbig2Bitmap(int width, int height)
    {
        Width = width;
        Height = height;
        _pixels = new byte[(long)width * height <= int.MaxValue ? width * height : 0];
    }

    internal int Width { get; }

    internal int Height { get; }

    /// <summary>Reads a pixel, answering zero for anything outside the bitmap (§6.2.5.2).</summary>
    internal int this[int x, int y]
    {
        get => x >= 0 && x < Width && y >= 0 && y < Height ? _pixels[(y * Width) + x] : 0;
        set => _pixels[(y * Width) + x] = (byte)value;
    }

    /// <summary>Copies a whole row from the one above it, which is what a typical row means.</summary>
    internal void CopyRowFromAbove(int y)
    {
        if (y <= 0)
            return;

        Array.Copy(_pixels, (y - 1) * Width, _pixels, y * Width, Width);
    }

    /// <summary>Fills the bitmap with one value, used to clear a page to its default pixel.</summary>
    internal void Fill(int value) => Array.Fill(_pixels, (byte)value);

    /// <summary>
    /// Takes a rectangular copy. A refinement region that names no earlier region refines what is
    /// already on the page underneath it, which is the area this returns (§7.4.7.4).
    /// </summary>
    /// <param name="x">The left edge of the area.</param>
    /// <param name="y">The top edge of the area.</param>
    /// <param name="width">How wide a copy to take.</param>
    /// <param name="height">How tall a copy to take.</param>
    internal Jbig2Bitmap Crop(int x, int y, int width, int height)
    {
        var copy = new Jbig2Bitmap(width, height);

        for (int row = 0; row < height; row++)
        {
            for (int column = 0; column < width; column++)
                copy[column, row] = this[x + column, y + row];
        }

        return copy;
    }

    /// <summary>
    /// Draws another bitmap onto this one at an offset, combining with the operator the region
    /// segment named (§7.4.1.5).
    /// </summary>
    internal void Combine(Jbig2Bitmap source, int x0, int y0, Jbig2CombinationOperator @operator)
    {
        for (int y = 0; y < source.Height; y++)
        {
            int targetY = y0 + y;
            if (targetY < 0 || targetY >= Height)
                continue;

            for (int x = 0; x < source.Width; x++)
            {
                int targetX = x0 + x;
                if (targetX < 0 || targetX >= Width)
                    continue;

                int existing = _pixels[(targetY * Width) + targetX];
                int incoming = source._pixels[(y * source.Width) + x];

                _pixels[(targetY * Width) + targetX] = (byte)(@operator switch
                {
                    Jbig2CombinationOperator.And => existing & incoming,
                    Jbig2CombinationOperator.Xor => existing ^ incoming,
                    Jbig2CombinationOperator.Xnor => 1 - (existing ^ incoming),
                    Jbig2CombinationOperator.Replace => incoming,
                    _ => existing | incoming,
                });
            }
        }
    }

    /// <summary>
    /// Packs the bitmap the way PDF wants an image of one bit per pixel: rows padded to a byte
    /// boundary, and a set bit meaning black.
    /// </summary>
    internal byte[] ToPackedRows()
    {
        int stride = (Width + 7) / 8;
        byte[] packed = new byte[stride * Height];

        for (int y = 0; y < Height; y++)
        {
            int row = y * stride;
            int source = y * Width;

            for (int x = 0; x < Width; x++)
            {
                if (_pixels[source + x] != 0)
                    packed[row + (x >> 3)] |= (byte)(0x80 >> (x & 7));
            }
        }

        return packed;
    }
}

/// <summary>How a region's bitmap is drawn onto the page (§7.4.1.5).</summary>
internal enum Jbig2CombinationOperator
{
    /// <summary>Set a pixel where either has it.</summary>
    Or = 0,

    /// <summary>Set a pixel only where both have it.</summary>
    And = 1,

    /// <summary>Set a pixel where exactly one has it.</summary>
    Xor = 2,

    /// <summary>Set a pixel where both agree.</summary>
    Xnor = 3,

    /// <summary>Overwrite whatever was there.</summary>
    Replace = 4,
}
