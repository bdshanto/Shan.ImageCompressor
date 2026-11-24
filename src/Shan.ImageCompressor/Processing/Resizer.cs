namespace Shan.ImageCompressor.Processing;

/// <summary>Shrinks an image (exact area averaging by default, or a bilinear/bicubic/Lanczos filter), weighting colour by alpha so transparent pixels don't bleed into edges.</summary>
internal static class Resizer
{
    public static RgbaImage Shrink(RgbaImage source, int width, int height, ResizeFilter filter = ResizeFilter.Box)
    {
        if (width >= source.Width && height >= source.Height) return source;
        width = Math.Clamp(width, 1, source.Width);
        height = Math.Clamp(height, 1, source.Height);

        var horizontal = ResampleRows(source.Pixels, source.Width, source.Height, width, filter);
        var transposed = Transpose(horizontal, width, source.Height);
        var vertical = ResampleRows(transposed, source.Height, width, height, filter);
        return new RgbaImage(width, height, Transpose(vertical, height, width));
    }

    /// <summary>Resamples every row of a <paramref name="width"/>×<paramref name="rows"/> RGBA buffer down to <paramref name="newWidth"/> samples.</summary>
    private static byte[] ResampleRows(byte[] src, int width, int rows, int newWidth, ResizeFilter filter)
    {
        var dst = new byte[checked(newWidth * rows * 4)];
        var (first, weights) = Weights(width, newWidth, filter);

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
                    dst[o] = ToByte(r / a);
                    dst[o + 1] = ToByte(g / a);
                    dst[o + 2] = ToByte(b / a);
                }
                dst[o + 3] = ToByte(a / total);
            }
        });
        return dst;
    }

    /// <summary>Rounds to a byte, clamping: the bicubic and Lanczos filters have negative lobes that can overshoot 0..255.</summary>
    private static byte ToByte(float v) => v <= 0 ? (byte)0 : v >= 255f ? (byte)255 : (byte)(v + 0.5f);

    /// <summary>For each output sample, the first source index it reads and the weights of the consecutive samples from there.</summary>
    private static (int[] First, float[][] Weights) Weights(int width, int newWidth, ResizeFilter filter)
    {
        var scale = (double)width / newWidth;
        var first = new int[newWidth];
        var weights = new float[newWidth][];

        if (filter == ResizeFilter.Box)
        {
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
            return (first, weights);
        }

        // The kernel is stretched by the reduction ratio so every source sample under it contributes (no aliasing), then normalised.
        var stretch = Math.Max(scale, 1);
        var support = Support(filter) * stretch;
        for (var i = 0; i < newWidth; i++)
        {
            var centre = (i + 0.5) * scale;
            var start = Math.Max(0, (int)Math.Floor(centre - support));
            var end = Math.Min(width - 1, (int)Math.Ceiling(centre + support) - 1);
            if (end < start) start = end = Math.Clamp((int)centre, 0, width - 1);

            var w = new float[end - start + 1];
            double sum = 0;
            for (var j = start; j <= end; j++)
            {
                var weight = Kernel(filter, (j + 0.5 - centre) / stretch);
                w[j - start] = (float)weight;
                sum += weight;
            }
            if (Math.Abs(sum) < 1e-9) { Array.Clear(w); w[Math.Clamp((int)centre, start, end) - start] = 1; sum = 1; }
            for (var j = 0; j < w.Length; j++) w[j] = (float)(w[j] / sum);
            first[i] = start;
            weights[i] = w;
        }
        return (first, weights);
    }

    private static double Support(ResizeFilter filter) => filter switch
    {
        ResizeFilter.Triangle => 1,
        ResizeFilter.CatmullRom => 2,
        _ => 3
    };

    private static double Kernel(ResizeFilter filter, double x)
    {
        x = Math.Abs(x);
        switch (filter)
        {
            case ResizeFilter.Triangle:
                return x < 1 ? 1 - x : 0;
            case ResizeFilter.CatmullRom: // cubic with B = 0, C = 1/2
                if (x < 1) return 1.5 * x * x * x - 2.5 * x * x + 1;
                if (x < 2) return -0.5 * x * x * x + 2.5 * x * x - 4 * x + 2;
                return 0;
            default: // Lanczos, three lobes
                if (x < 1e-9) return 1;
                if (x >= 3) return 0;
                var px = Math.PI * x;
                return 3 * Math.Sin(px) * Math.Sin(px / 3) / (px * px);
        }
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
