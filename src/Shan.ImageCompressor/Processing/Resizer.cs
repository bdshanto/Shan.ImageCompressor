namespace Shan.ImageCompressor.Processing;

/// <summary>Shrinks an image by exact area averaging (a box filter), weighting colour by alpha so transparent pixels don't bleed into edges.</summary>
internal static class Resizer
{
    public static RgbaImage Shrink(RgbaImage source, int width, int height)
    {
        if (width >= source.Width && height >= source.Height) return source;
        width = Math.Clamp(width, 1, source.Width);
        height = Math.Clamp(height, 1, source.Height);

        var horizontal = ResampleRows(source.Pixels, source.Width, source.Height, width);
        var transposed = Transpose(horizontal, width, source.Height);
        var vertical = ResampleRows(transposed, source.Height, width, height);
        return new RgbaImage(width, height, Transpose(vertical, height, width));
    }

    /// <summary>Resamples every row of a <paramref name="width"/>×<paramref name="rows"/> RGBA buffer down to <paramref name="newWidth"/> samples.</summary>
    private static byte[] ResampleRows(byte[] src, int width, int rows, int newWidth)
    {
        var dst = new byte[checked(newWidth * rows * 4)];
        var scale = (double)width / newWidth;

        var first = new int[newWidth];
        var weights = new float[newWidth][];
        for (var i = 0; i < newWidth; i++)
        {
            double lo = i * scale, hi = (i + 1) * scale;
            var start = (int)Math.Floor(lo);
            var end = Math.Min(width - 1, (int)Math.Ceiling(hi) - 1);
            first[i] = start;
            weights[i] = new float[end - start + 1];
            for (var j = start; j <= end; j++)
                weights[i][j - start] = (float)(Math.Min(hi, j + 1) - Math.Max(lo, j));
        }

        Work.Rows(rows, y =>
        {
            var rowStart = y * width * 4;
            var outStart = y * newWidth * 4;
            for (var i = 0; i < newWidth; i++)
            {
                float total = 0, a = 0, r = 0, g = 0, b = 0;
                var w = weights[i];
                var p = rowStart + first[i] * 4;
                for (var j = 0; j < w.Length; j++, p += 4)
                {
                    var weight = w[j];
                    var alpha = src[p + 3] * weight;
                    total += weight;
                    a += alpha;
                    r += src[p] * alpha;
                    g += src[p + 1] * alpha;
                    b += src[p + 2] * alpha;
                }

                var o = outStart + i * 4;
                if (a > 0)
                {
                    dst[o] = (byte)(r / a + 0.5f);
                    dst[o + 1] = (byte)(g / a + 0.5f);
                    dst[o + 2] = (byte)(b / a + 0.5f);
                }
                dst[o + 3] = (byte)(a / total + 0.5f);
            }
        });
        return dst;
    }

    private static byte[] Transpose(byte[] src, int width, int height)
    {
        var dst = new byte[src.Length];
        const int Tile = 32; // blocked so both the reads and the writes stay in cache
        var tilesY = (height + Tile - 1) / Tile;
        Work.Rows(tilesY, ty =>
        {
            var from = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(src.AsSpan());
            var to = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(dst.AsSpan());
            for (var x0 = 0; x0 < width; x0 += Tile)
                for (var y = ty * Tile; y < Math.Min(height, ty * Tile + Tile); y++)
                    for (var x = x0; x < Math.Min(width, x0 + Tile); x++)
                        to[x * height + y] = from[y * width + x];
        });
        return dst;
    }
}
