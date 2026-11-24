using System.Buffers.Binary;

namespace Shan.ImageCompressor.Qoi;

/// <summary>Decodes QOI ("Quite OK Image") files, 3 or 4 channels.</summary>
internal static class QoiDecoder
{
    public static RgbaImage Decode(byte[] data, long maxPixels)
    {
        try { return DecodeCore(data, maxPixels); }
        catch (Exception e) when (e is IndexOutOfRangeException or ArgumentException or OverflowException)
        {
            throw new InvalidDataException("The QOI data is corrupt.", e);
        }
    }

    private static RgbaImage DecodeCore(byte[] data, long maxPixels)
    {
        if (data.Length < 14 + 8 || !data.AsSpan(0, 4).SequenceEqual("qoif"u8)) throw new InvalidDataException("Not a QOI image.");
        var width = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4));
        var height = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(8));
        var channels = data[12];
        if (width == 0 || height == 0 || width > int.MaxValue || height > int.MaxValue) throw new InvalidDataException("The QOI has invalid dimensions.");
        if ((long)width * height > maxPixels) throw new InvalidDataException($"The image has {(long)width * height} pixels, over the {maxPixels} limit.");
        if (channels is not (3 or 4)) throw new InvalidDataException("The QOI has an invalid channel count.");

        var image = RgbaImage.Create((int)width, (int)height);
        var pixels = image.Pixels;
        var index = new byte[64 * 4];
        byte r = 0, g = 0, b = 0, a = 255;
        var pos = 14;
        var end = data.Length - 8; // the 8-byte end marker
        var run = 0;

        for (var o = 0; o < pixels.Length; o += 4)
        {
            if (run > 0) run--;
            else if (pos < end)
            {
                var op = data[pos++];
                if (op == 0xFE) { r = data[pos++]; g = data[pos++]; b = data[pos++]; }
                else if (op == 0xFF) { r = data[pos++]; g = data[pos++]; b = data[pos++]; a = data[pos++]; }
                else switch (op >> 6)
                {
                    case 0:
                        r = index[op * 4]; g = index[op * 4 + 1]; b = index[op * 4 + 2]; a = index[op * 4 + 3];
                        break;
                    case 1:
                        r += (byte)(((op >> 4) & 3) - 2);
                        g += (byte)(((op >> 2) & 3) - 2);
                        b += (byte)((op & 3) - 2);
                        break;
                    case 2:
                    {
                        var next = data[pos++];
                        var dg = (op & 0x3F) - 32;
                        r += (byte)(dg - 8 + ((next >> 4) & 15));
                        g += (byte)dg;
                        b += (byte)(dg - 8 + (next & 15));
                        break;
                    }
                    default:
                        run = op & 0x3F;
                        break;
                }
                var slot = (r * 3 + g * 5 + b * 7 + a * 11) % 64 * 4;
                index[slot] = r; index[slot + 1] = g; index[slot + 2] = b; index[slot + 3] = a;
            }
            else throw new InvalidDataException("The QOI pixel data is truncated.");

            pixels[o] = r; pixels[o + 1] = g; pixels[o + 2] = b; pixels[o + 3] = a;
        }
        return image;
    }
}
