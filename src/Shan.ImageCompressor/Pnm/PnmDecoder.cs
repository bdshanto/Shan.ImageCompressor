namespace Shan.ImageCompressor.Pnm;

/// <summary>Decodes the Netpbm family: PBM (P1/P4), PGM (P2/P5) and PPM (P3/P6), ASCII or binary, any max value up to 65535.</summary>
internal static class PnmDecoder
{
    public static RgbaImage Decode(byte[] data, long maxPixels)
    {
        try { return DecodeCore(data, maxPixels); }
        catch (Exception e) when (e is IndexOutOfRangeException or ArgumentException or OverflowException)
        {
            throw new InvalidDataException("The PNM data is corrupt.", e);
        }
    }

    private static RgbaImage DecodeCore(byte[] data, long maxPixels)
    {
        if (data.Length < 3 || data[0] != (byte)'P' || data[1] is < (byte)'1' or > (byte)'6') throw new InvalidDataException("Not a PNM image.");
        var kind = data[1] - '0';
        var bitmap = kind is 1 or 4;
        var ascii = kind is 1 or 2 or 3;
        var channels = kind is 3 or 6 ? 3 : 1;

        var pos = 2;
        var width = ReadNumber(data, ref pos);
        var height = ReadNumber(data, ref pos);
        var maxValue = bitmap ? 1 : ReadNumber(data, ref pos);
        if (width <= 0 || height <= 0) throw new InvalidDataException("The PNM has invalid dimensions.");
        if ((long)width * height > maxPixels) throw new InvalidDataException($"The image has {(long)width * height} pixels, over the {maxPixels} limit.");
        if (maxValue is < 1 or > 65535) throw new InvalidDataException("The PNM has an invalid maximum value.");
        if (!ascii) pos++; // exactly one whitespace byte separates the header from binary pixel data

        var image = RgbaImage.Create(width, height);
        var pixels = image.Pixels;
        var wide = maxValue > 255;

        if (kind == 4)
        {
            var rowBytes = (width + 7) / 8;
            if (pos + (long)rowBytes * height > data.Length) throw new InvalidDataException("The PNM pixel data is truncated.");
            for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                {
                    var black = (data[pos + y * rowBytes + (x >> 3)] >> (7 - (x & 7))) & 1;
                    Set(pixels, (y * width + x) * 4, black == 1 ? (byte)0 : (byte)255, black == 1 ? (byte)0 : (byte)255, black == 1 ? (byte)0 : (byte)255);
                }
            return image;
        }

        if (!ascii && pos + (long)width * height * channels * (wide ? 2 : 1) > data.Length) throw new InvalidDataException("The PNM pixel data is truncated.");

        Span<byte> rgb = stackalloc byte[3];
        for (var i = 0; i < width * height; i++)
        {
            for (var c = 0; c < channels; c++)
            {
                int value;
                if (kind == 1) value = ReadBit(data, ref pos);
                else if (ascii) value = ReadNumber(data, ref pos);
                else if (wide) { value = data[pos] << 8 | data[pos + 1]; pos += 2; }
                else value = data[pos++];

                if (kind == 1) rgb[c] = value == 1 ? (byte)0 : (byte)255; // in PBM a 1 is black
                else rgb[c] = (byte)Math.Min(255, (value * 255 + maxValue / 2) / maxValue);
            }
            if (channels == 1) rgb[1] = rgb[2] = rgb[0];
            Set(pixels, i * 4, rgb[0], rgb[1], rgb[2]);
        }
        return image;
    }

    private static void Set(byte[] pixels, int o, byte r, byte g, byte b)
    {
        pixels[o] = r;
        pixels[o + 1] = g;
        pixels[o + 2] = b;
        pixels[o + 3] = 255;
    }

    /// <summary>Skips whitespace and <c>#</c> comments, then reads a decimal number.</summary>
    private static int ReadNumber(byte[] data, ref int pos)
    {
        SkipSeparators(data, ref pos);
        if (pos >= data.Length || data[pos] is < (byte)'0' or > (byte)'9') throw new InvalidDataException("The PNM header is invalid.");
        long value = 0;
        while (pos < data.Length && data[pos] is >= (byte)'0' and <= (byte)'9')
        {
            value = value * 10 + (data[pos++] - '0');
            if (value > int.MaxValue) throw new InvalidDataException("The PNM header is invalid.");
        }
        return (int)value;
    }

    /// <summary>ASCII PBM allows the bits to run together with no separators.</summary>
    private static int ReadBit(byte[] data, ref int pos)
    {
        SkipSeparators(data, ref pos);
        if (pos >= data.Length || data[pos] is not ((byte)'0' or (byte)'1')) throw new InvalidDataException("The PNM pixel data is invalid.");
        return data[pos++] - '0';
    }

    private static void SkipSeparators(byte[] data, ref int pos)
    {
        while (pos < data.Length)
        {
            if (data[pos] == (byte)'#') { while (pos < data.Length && data[pos] != '\n') pos++; }
            else if (data[pos] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') pos++;
            else break;
        }
    }
}
