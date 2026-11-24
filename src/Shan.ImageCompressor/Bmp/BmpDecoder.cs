using System.Buffers.Binary;

namespace Shan.ImageCompressor.Bmp;

/// <summary>
/// Decodes Windows BMP files to 8-bit RGBA: 1/4/8-bit palette, 16/24/32-bit, BI_RGB, BI_BITFIELDS (and the alpha variant),
/// RLE4/RLE8, top-down or bottom-up, from the OS/2 core header through the V5 header.
/// </summary>
internal sealed class BmpDecoder
{
    private const int FileHeaderSize = 14;

    public static RgbaImage Decode(byte[] data, long maxPixels)
    {
        try { return DecodeCore(data, maxPixels); }
        catch (Exception e) when (e is IndexOutOfRangeException or ArgumentException or OverflowException)
        {
            throw new InvalidDataException("The BMP data is corrupt.", e);
        }
    }

    private static RgbaImage DecodeCore(byte[] data, long maxPixels)
    {
        if (data.Length < FileHeaderSize + 12 || data[0] != (byte)'B' || data[1] != (byte)'M') throw new InvalidDataException("Not a BMP.");

        var pixelOffset = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(10));
        var headerSize = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(FileHeaderSize));
        if (headerSize < 12 || FileHeaderSize + headerSize > data.Length) throw new InvalidDataException("The BMP header is truncated.");

        int width, height, bitCount, compression = 0, colorsUsed = 0;
        var paletteEntrySize = 4;
        if (headerSize == 12) // OS/2 BITMAPCOREHEADER: 16-bit dimensions, 3-byte palette entries
        {
            width = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(FileHeaderSize + 4));
            height = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(FileHeaderSize + 6));
            bitCount = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(FileHeaderSize + 10));
            paletteEntrySize = 3;
        }
        else
        {
            if (headerSize < 40) throw new InvalidDataException("The BMP header is not recognised.");
            width = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(FileHeaderSize + 4));
            height = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(FileHeaderSize + 8));
            bitCount = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(FileHeaderSize + 14));
            compression = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(FileHeaderSize + 16));
            colorsUsed = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(FileHeaderSize + 32));
        }

        var topDown = height < 0;
        if (height == int.MinValue) throw new InvalidDataException("The BMP has invalid dimensions.");
        height = Math.Abs(height);
        if (width <= 0 || height <= 0) throw new InvalidDataException("The BMP has invalid dimensions.");
        if ((long)width * height > maxPixels) throw new InvalidDataException($"The image has {(long)width * height} pixels, over the {maxPixels} limit.");
        if (bitCount is not (1 or 4 or 8 or 16 or 24 or 32)) throw new NotSupportedException($"{bitCount}-bit BMPs are not supported.");

        var isRle = compression is 1 or 2;
        if (compression is not (0 or 1 or 2 or 3 or 6)) throw new NotSupportedException("This BMP compression method (JPEG/PNG-in-BMP, CMYK) is not supported.");
        if (compression == 1 && bitCount != 8 || compression == 2 && bitCount != 4) throw new InvalidDataException("The BMP compression does not match its bit depth.");
        if (compression is 3 or 6 && bitCount is not (16 or 32)) throw new InvalidDataException("The BMP bit masks do not match its bit depth.");

        // Colour masks: explicit for BITMAPINFOHEADER+BI_BITFIELDS (12 or 16 bytes after the header), inside the V4/V5 header otherwise.
        uint redMask = 0, greenMask = 0, blueMask = 0, alphaMask = 0;
        var masksEnd = FileHeaderSize + headerSize;
        if (bitCount is 16 or 32)
        {
            if (compression is 3 or 6)
            {
                var maskOffset = headerSize >= 52 ? FileHeaderSize + 40 : FileHeaderSize + headerSize;
                var maskCount = compression == 6 || headerSize >= 56 ? 4 : 3;
                if (maskOffset + maskCount * 4 > data.Length) throw new InvalidDataException("The BMP bit masks are truncated.");
                redMask = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(maskOffset));
                greenMask = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(maskOffset + 4));
                blueMask = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(maskOffset + 8));
                if (maskCount == 4) alphaMask = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(maskOffset + 12));
                if (headerSize < 52) masksEnd += maskCount * 4;
            }
            else if (bitCount == 16) { redMask = 0x7C00; greenMask = 0x03E0; blueMask = 0x001F; }
            else { redMask = 0x00FF0000; greenMask = 0x0000FF00; blueMask = 0x000000FF; }
        }

        byte[]? palette = null; // RGBA, 4 bytes per entry
        if (bitCount <= 8)
        {
            var entries = colorsUsed > 0 ? Math.Min(colorsUsed, 1 << bitCount) : 1 << bitCount;
            var paletteStart = FileHeaderSize + headerSize;
            if (paletteStart + (long)entries * paletteEntrySize > data.Length) throw new InvalidDataException("The BMP palette is truncated.");
            palette = new byte[(1 << bitCount) * 4]; // entries beyond the stored ones stay opaque black
            for (var i = 0; i < palette.Length; i += 4) palette[i + 3] = 255;
            for (var i = 0; i < entries; i++)
            {
                var o = paletteStart + i * paletteEntrySize;
                palette[i * 4] = data[o + 2];
                palette[i * 4 + 1] = data[o + 1];
                palette[i * 4 + 2] = data[o];
            }
            masksEnd = paletteStart + entries * paletteEntrySize;
        }

        if (pixelOffset < masksEnd || pixelOffset > data.Length) pixelOffset = masksEnd; // a bad offset field: fall back to "right after the headers"

        var image = RgbaImage.Create(width, height);
        if (isRle) DecodeRle(data, pixelOffset, image, palette!, bitCount == 4, topDown);
        else DecodeRaw(data, pixelOffset, image, palette, bitCount, topDown, redMask, greenMask, blueMask, alphaMask);
        return image;
    }

    private static void DecodeRaw(byte[] data, int offset, RgbaImage image, byte[]? palette, int bitCount, bool topDown,
        uint redMask, uint greenMask, uint blueMask, uint alphaMask)
    {
        var width = image.Width;
        var height = image.Height;
        var stride = checked((int)(((long)width * bitCount + 31) / 32 * 4));
        if (offset + (long)stride * height > data.Length) throw new InvalidDataException("The BMP pixel data is truncated.");

        var hasAlpha = false;
        for (var row = 0; row < height; row++)
        {
            var y = topDown ? row : height - 1 - row;
            var source = offset + row * stride;
            var target = y * width * 4;
            for (var x = 0; x < width; x++)
            {
                var o = target + x * 4;
                switch (bitCount)
                {
                    case 1 or 4 or 8:
                    {
                        var bit = x * bitCount;
                        var b = data[source + (bit >> 3)];
                        var index = bitCount == 8 ? b : (b >> (8 - bitCount - (bit & 7))) & ((1 << bitCount) - 1);
                        Buffer.BlockCopy(palette!, index * 4, image.Pixels, o, 4);
                        break;
                    }
                    case 24:
                        image.Pixels[o] = data[source + x * 3 + 2];
                        image.Pixels[o + 1] = data[source + x * 3 + 1];
                        image.Pixels[o + 2] = data[source + x * 3];
                        image.Pixels[o + 3] = 255;
                        break;
                    default: // 16 or 32 bit, through the masks
                    {
                        var value = bitCount == 16
                            ? BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(source + x * 2))
                            : BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(source + x * 4));
                        image.Pixels[o] = Scale(value, redMask);
                        image.Pixels[o + 1] = Scale(value, greenMask);
                        image.Pixels[o + 2] = Scale(value, blueMask);
                        var alpha = alphaMask == 0 ? (byte)255 : Scale(value, alphaMask);
                        image.Pixels[o + 3] = alpha;
                        if (alpha != 0) hasAlpha = true;
                        break;
                    }
                }
            }
        }

        // A 32-bit file whose alpha channel is all zero almost always means "alpha unused" (older writers), not "fully transparent".
        if (alphaMask != 0 && !hasAlpha)
            for (var i = 3; i < image.Pixels.Length; i += 4) image.Pixels[i] = 255;
    }

    /// <summary>Extracts the bits under <paramref name="mask"/> and stretches them to 0–255.</summary>
    private static byte Scale(uint value, uint mask)
    {
        if (mask == 0) return 0;
        var shift = System.Numerics.BitOperations.TrailingZeroCount(mask);
        var bits = System.Numerics.BitOperations.PopCount(mask >> shift);
        var field = (value & mask) >> shift;
        var max = (1u << bits) - 1;
        return max == 0 ? (byte)0 : (byte)((field * 255u + max / 2) / max);
    }

    private static void DecodeRle(byte[] data, int offset, RgbaImage image, byte[] palette, bool fourBit, bool topDown)
    {
        var width = image.Width;
        var height = image.Height;
        for (var i = 0; i < width * height; i++) Buffer.BlockCopy(palette, 0, image.Pixels, i * 4, 4); // uncovered pixels: palette entry 0

        int x = 0, row = 0, pos = offset;
        void Put(int index)
        {
            if (x < width && row < height)
            {
                var y = topDown ? row : height - 1 - row;
                Buffer.BlockCopy(palette, (index & (palette.Length / 4 - 1)) * 4, image.Pixels, (y * width + x) * 4, 4);
            }
            x++;
        }

        while (pos + 1 < data.Length && row < height)
        {
            var count = data[pos++];
            var value = data[pos++];
            if (count > 0) // an encoded run
            {
                for (var i = 0; i < count; i++)
                    Put(fourBit ? (i % 2 == 0 ? value >> 4 : value & 15) : value);
                continue;
            }

            switch (value)
            {
                case 0: x = 0; row++; break;        // end of line
                case 1: return;                     // end of bitmap
                case 2:                             // delta
                    if (pos + 1 >= data.Length) return;
                    x += data[pos++];
                    row += data[pos++];
                    break;
                default:                            // an absolute run of `value` pixels, padded to a 16-bit boundary
                {
                    var bytes = fourBit ? (value + 1) / 2 : value;
                    if (pos + bytes > data.Length) throw new InvalidDataException("The BMP pixel data is truncated.");
                    for (var i = 0; i < value; i++)
                    {
                        var b = data[pos + (fourBit ? i / 2 : i)];
                        Put(fourBit ? (i % 2 == 0 ? b >> 4 : b & 15) : b);
                    }
                    pos += bytes + (bytes & 1);
                    break;
                }
            }
        }
    }
}
