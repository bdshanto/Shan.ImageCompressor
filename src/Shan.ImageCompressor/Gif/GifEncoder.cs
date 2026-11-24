using System.Buffers.Binary;

namespace Shan.ImageCompressor.Gif;

/// <summary>
/// Writes a GIF89a from full-screen frames, using one global palette. An opaque animation stores each frame as only the rectangle that
/// changed, with unchanged pixels set to the transparent index; one with see-through pixels stores whole frames, each cleared before the next.
/// </summary>
internal static class GifEncoder
{
    /// <param name="frames">At least one frame, all the same size.</param>
    /// <param name="loop">The NETSCAPE repeat count (0 = forever), or -1 to write no loop extension (plays once).</param>
    /// <param name="colours">The palette budget, 2–256, counting the entry kept for transparency.</param>
    public static MemoryStream Encode(IReadOnlyList<GifFrame> frames, int loop, int colours)
    {
        var width = frames[0].Image.Width;
        var height = frames[0].Image.Height;
        var images = frames.Select(f => f.Image).ToList();

        var hasTransparency = images.Any(HasSeeThroughPixels);
        var animated = frames.Count > 1;
        var reserve = hasTransparency || animated; // one palette slot is kept for "no change / see-through"

        var quantizer = GifQuantizer.Build(images, Math.Clamp(colours - (reserve ? 1 : 0), 1, 255));
        var paletteCount = quantizer.Palette.Length / 3;
        var transparentIndex = reserve ? paletteCount : -1;
        var bits = 1;
        while (1 << bits < paletteCount + (reserve ? 1 : 0)) bits++;

        var indexed = new byte[frames.Count][];
        Work.Rows(frames.Count, f => indexed[f] = Index(images[f], quantizer, transparentIndex));

        var output = new MemoryStream();
        output.Write("GIF89a"u8);
        WriteUInt16(output, width);
        WriteUInt16(output, height);
        output.WriteByte((byte)(0x80 | ((bits - 1) << 4) | (bits - 1)));
        output.WriteByte(0); // background colour index
        output.WriteByte(0); // pixel aspect ratio
        var table = new byte[(1 << bits) * 3];
        quantizer.Palette.CopyTo(table, 0);
        output.Write(table);

        if (animated && loop >= 0)
        {
            output.Write([0x21, 0xFF, 0x0B]);
            output.Write("NETSCAPE2.0"u8);
            output.Write([0x03, 0x01]);
            WriteUInt16(output, Math.Min(loop, 65535));
            output.WriteByte(0);
        }

        var minCodeSize = Math.Max(2, bits);
        for (var f = 0; f < frames.Count; f++)
        {
            int left = 0, top = 0, w = width, h = height;
            var pixels = indexed[f];

            if (animated && !hasTransparency && f > 0)
            {
                // only what differs from the frame before; everything else is left showing through
                var before = indexed[f - 1];
                int minX = width, minY = height, maxX = -1, maxY = -1;
                for (var y = 0; y < height; y++)
                    for (var x = 0; x < width; x++)
                        if (pixels[y * width + x] != before[y * width + x])
                        {
                            if (x < minX) minX = x;
                            if (x > maxX) maxX = x;
                            if (y < minY) minY = y;
                            maxY = y;
                        }

                if (maxX < 0) { left = top = 0; w = h = 1; pixels = [(byte)transparentIndex]; } // identical frame: one invisible pixel
                else
                {
                    left = minX; top = minY; w = maxX - minX + 1; h = maxY - minY + 1;
                    var sub = new byte[w * h];
                    for (var y = 0; y < h; y++)
                        for (var x = 0; x < w; x++)
                        {
                            var i = (top + y) * width + left + x;
                            sub[y * w + x] = pixels[i] == before[i] ? (byte)transparentIndex : pixels[i];
                        }
                    pixels = sub;
                }
            }

            var disposal = hasTransparency ? 2 : 1;
            output.Write([0x21, 0xF9, 0x04, (byte)(disposal << 2 | (reserve ? 1 : 0))]);
            WriteUInt16(output, Math.Clamp(frames[f].DelayCentiseconds, 0, 65535));
            output.WriteByte((byte)(reserve ? transparentIndex : 0));
            output.WriteByte(0);

            output.WriteByte(0x2C);
            WriteUInt16(output, left);
            WriteUInt16(output, top);
            WriteUInt16(output, w);
            WriteUInt16(output, h);
            output.WriteByte(0); // no local palette, not interlaced
            GifLzwEncoder.Write(output, pixels, minCodeSize);
        }

        output.WriteByte(0x3B);
        output.Position = 0;
        return output;
    }

    private static bool HasSeeThroughPixels(RgbaImage image)
    {
        var p = image.Pixels;
        for (var i = 3; i < p.Length; i += 4)
            if (p[i] < 128) return true;
        return false;
    }

    private static byte[] Index(RgbaImage image, GifQuantizer quantizer, int transparentIndex)
    {
        var p = image.Pixels;
        var result = new byte[image.Width * image.Height];
        for (var i = 0; i < result.Length; i++)
        {
            var o = i * 4;
            result[i] = p[o + 3] < 128 ? (byte)transparentIndex : (byte)quantizer.Nearest(p[o], p[o + 1], p[o + 2]);
        }
        return result;
    }

    private static void WriteUInt16(Stream stream, int value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, (ushort)value);
        stream.Write(bytes);
    }
}
