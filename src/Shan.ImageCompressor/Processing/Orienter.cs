using System.Runtime.InteropServices;

namespace Shan.ImageCompressor.Processing;

/// <summary>Applies an EXIF orientation (1–8) so the pixels are stored the right way up.</summary>
internal static class Orienter
{
    public static RgbaImage Apply(RgbaImage source, int orientation)
    {
        if (orientation is < 2 or > 8) return source;

        var w = source.Width;
        var h = source.Height;
        var swap = orientation >= 5;
        var outW = swap ? h : w;
        var outH = swap ? w : h;

        var result = RgbaImage.Create(outW, outH);

        Work.Rows(outH, oy =>
        {
            var srcPixels = MemoryMarshal.Cast<byte, uint>(source.Pixels.AsSpan());
            var dstPixels = MemoryMarshal.Cast<byte, uint>(result.Pixels.AsSpan());
            for (var ox = 0; ox < outW; ox++)
            {
                var (sx, sy) = orientation switch
                {
                    2 => (w - 1 - ox, oy),
                    3 => (w - 1 - ox, h - 1 - oy),
                    4 => (ox, h - 1 - oy),
                    5 => (oy, ox),
                    6 => (oy, h - 1 - ox),
                    7 => (w - 1 - oy, h - 1 - ox),
                    _ => (w - 1 - oy, ox)
                };
                dstPixels[oy * outW + ox] = srcPixels[sy * w + sx];
            }
        });
        return result;
    }
}
