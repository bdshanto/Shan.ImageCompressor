using System.Buffers.Binary;

namespace Shan.ImageCompressor.Tga;

/// <summary>Decodes Truevision TGA: colour-mapped, true-colour and grayscale, 8/15/16/24/32-bit, uncompressed or RLE, any origin.</summary>
internal static class TgaDecoder
{
    /// <summary>
    /// TGA has no magic number, so recognition rests on a strict header check plus, for uncompressed files, a plausible file size.
    /// <paramref name="totalLength"/> is the whole file's length (the span may only be its first bytes).
    /// </summary>
    public static bool LooksLikeTga(ReadOnlySpan<byte> d, long totalLength)
    {
        if (d.Length < 18) return false;
        int idLength = d[0], mapType = d[1], type = d[2];
        var mapLength = BinaryPrimitives.ReadUInt16LittleEndian(d[5..]);
        int mapEntryBits = d[7];
        int width = BinaryPrimitives.ReadUInt16LittleEndian(d[12..]);
        int height = BinaryPrimitives.ReadUInt16LittleEndian(d[14..]);
        int bpp = d[16];
        var descriptor = d[17];

        if (mapType > 1 || type is not (1 or 2 or 3 or 9 or 10 or 11) || width == 0 || height == 0 || (descriptor & 0xC0) != 0) return false;
        var mapped = type is 1 or 9;
        if (mapped != (mapType == 1)) return false;
        if (mapped && (bpp != 8 || mapLength == 0 || mapEntryBits is not (15 or 16 or 24 or 32))) return false;
        if (!mapped && mapLength != 0) return false;
        if (type is 2 or 10 && bpp is not (15 or 16 or 24 or 32)) return false;
        if (type is 3 or 11 && bpp is not (8 or 16)) return false;

        if (type is 1 or 2 or 3) // uncompressed: the file must be at least as big as the pixels
        {
            var mapBytes = mapped ? mapLength * ((mapEntryBits + 7) / 8) : 0;
            if (totalLength < 18 + idLength + mapBytes + (long)width * height * ((bpp + 7) / 8)) return false;
        }
        return true;
    }

    public static RgbaImage Decode(byte[] data, long maxPixels)
    {
        try { return DecodeCore(data, maxPixels); }
        catch (Exception e) when (e is IndexOutOfRangeException or ArgumentException or OverflowException)
        {
            throw new InvalidDataException("The TGA data is corrupt.", e);
        }
    }

    private static RgbaImage DecodeCore(byte[] data, long maxPixels)
    {
        if (!LooksLikeTga(data, data.Length)) throw new InvalidDataException("Not a TGA image.");
        int idLength = data[0], type = data[2];
        int mapFirst = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(3));
        int mapLength = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(5));
        int mapEntryBits = data[7];
        int width = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(12));
        int height = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(14));
        int bpp = data[16];
        var descriptor = data[17];
        if ((long)width * height > maxPixels) throw new InvalidDataException($"The image has {(long)width * height} pixels, over the {maxPixels} limit.");

        var pos = 18 + idLength;
        var rle = type >= 9;
        var mapped = type is 1 or 9;
        var gray = type is 3 or 11;

        byte[]? palette = null; // RGBA
        if (mapped)
        {
            var entryBytes = (mapEntryBits + 7) / 8;
            if (pos + (long)mapLength * entryBytes > data.Length) throw new InvalidDataException("The TGA colour map is truncated.");
            palette = new byte[(mapFirst + mapLength) * 4];
            for (var i = 0; i < mapLength; i++)
                ReadColor(data, pos + i * entryBytes, mapEntryBits, descriptor & 0x0F, palette, (mapFirst + i) * 4);
            pos += mapLength * entryBytes;
        }

        var pixelBytes = (bpp + 7) / 8;
        var count = width * height;
        var raw = new byte[checked(count * pixelBytes)];
        if (!rle)
        {
            if (pos + raw.Length > data.Length) throw new InvalidDataException("The TGA pixel data is truncated.");
            Buffer.BlockCopy(data, pos, raw, 0, raw.Length);
        }
        else
        {
            for (var written = 0; written < count;)
            {
                if (pos >= data.Length) throw new InvalidDataException("The TGA pixel data is truncated.");
                var header = data[pos++];
                var run = (header & 0x7F) + 1;
                if (written + run > count) run = count - written; // a packet running past the end is clipped
                if ((header & 0x80) != 0)
                {
                    if (pos + pixelBytes > data.Length) throw new InvalidDataException("The TGA pixel data is truncated.");
                    for (var i = 0; i < run; i++) Buffer.BlockCopy(data, pos, raw, (written + i) * pixelBytes, pixelBytes);
                    pos += pixelBytes;
                }
                else
                {
                    if (pos + (long)run * pixelBytes > data.Length) throw new InvalidDataException("The TGA pixel data is truncated.");
                    Buffer.BlockCopy(data, pos, raw, written * pixelBytes, run * pixelBytes);
                    pos += run * pixelBytes;
                }
                written += run;
            }
        }

        var image = RgbaImage.Create(width, height);
        var topDown = (descriptor & 0x20) != 0;
        var rightToLeft = (descriptor & 0x10) != 0;
        var alphaBits = descriptor & 0x0F;
        var anyAlpha = false;

        for (var i = 0; i < count; i++)
        {
            var x = i % width;
            var y = i / width;
            var dx = rightToLeft ? width - 1 - x : x;
            var dy = topDown ? y : height - 1 - y;
            var o = (dy * width + dx) * 4;

            if (mapped)
            {
                var index = raw[i];
                if (index >= palette!.Length / 4) throw new InvalidDataException("The TGA refers to a colour outside its map.");
                Buffer.BlockCopy(palette, index * 4, image.Pixels, o, 4);
            }
            else if (gray)
            {
                image.Pixels[o] = image.Pixels[o + 1] = image.Pixels[o + 2] = raw[i * pixelBytes];
                image.Pixels[o + 3] = bpp == 16 ? raw[i * pixelBytes + 1] : (byte)255;
            }
            else ReadColor(raw, i * pixelBytes, bpp, alphaBits, image.Pixels, o);

            if (image.Pixels[o + 3] != 0) anyAlpha = true;
        }

        // Many writers leave the alpha channel zeroed ("unused"); an all-transparent picture is never what was meant.
        if (!anyAlpha) for (var i = 3; i < image.Pixels.Length; i += 4) image.Pixels[i] = 255;
        return image;
    }

    /// <summary>Reads one 15/16/24/32-bit colour (BGR order, little-endian) into RGBA.</summary>
    private static void ReadColor(byte[] source, int at, int bits, int alphaBits, byte[] target, int o)
    {
        switch (bits)
        {
            case 15 or 16:
            {
                var v = source[at] | source[at + 1] << 8;
                target[o] = Expand5((v >> 10) & 31);
                target[o + 1] = Expand5((v >> 5) & 31);
                target[o + 2] = Expand5(v & 31);
                target[o + 3] = bits == 16 && alphaBits > 0 ? (v & 0x8000) != 0 ? (byte)255 : (byte)0 : (byte)255;
                break;
            }
            case 24:
                target[o] = source[at + 2]; target[o + 1] = source[at + 1]; target[o + 2] = source[at]; target[o + 3] = 255;
                break;
            default: // 32
                target[o] = source[at + 2]; target[o + 1] = source[at + 1]; target[o + 2] = source[at];
                target[o + 3] = alphaBits > 0 ? source[at + 3] : (byte)255;
                break;
        }
    }

    private static byte Expand5(int v) => (byte)((v << 3) | (v >> 2));
}
