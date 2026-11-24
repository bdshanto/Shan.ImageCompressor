using System.Buffers.Binary;
using System.IO.Compression;

namespace Shan.ImageCompressor.Tiff;

/// <summary>
/// Decodes the first image of a classic (non-Big) TIFF: strips, planar-contiguous, 1/2/4/8/16-bit grayscale, palette, RGB(A) and CMYK,
/// uncompressed, PackBits, LZW or Deflate, with the horizontal predictor. Tiled, planar-separate, floating-point and JPEG/CCITT-compressed
/// files are rejected with <see cref="NotSupportedException"/>.
/// </summary>
internal sealed class TiffDecoder
{
    private readonly byte[] _data;
    private readonly bool _little;

    private TiffDecoder(byte[] data, bool little)
    {
        _data = data;
        _little = little;
    }

    public static RgbaImage Decode(byte[] data, long maxPixels, out int orientation)
    {
        try
        {
            if (data.Length < 8) throw new InvalidDataException("Not a TIFF.");
            bool little;
            if (data[0] == (byte)'I' && data[1] == (byte)'I') little = true;
            else if (data[0] == (byte)'M' && data[1] == (byte)'M') little = false;
            else throw new InvalidDataException("Not a TIFF.");

            var decoder = new TiffDecoder(data, little);
            var magic = decoder.U16(2);
            if (magic == 43) throw new NotSupportedException("BigTIFF files are not supported.");
            if (magic != 42) throw new InvalidDataException("Not a TIFF.");
            return decoder.DecodeCore(maxPixels, out orientation);
        }
        catch (Exception e) when (e is IndexOutOfRangeException or ArgumentException or OverflowException or IOException)
        {
            throw new InvalidDataException("The TIFF data is corrupt.", e);
        }
    }

    private ushort U16(long offset) => _little ? BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(Check(offset, 2))) : BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(Check(offset, 2)));
    private uint U32(long offset) => _little ? BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(Check(offset, 4))) : BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(Check(offset, 4)));

    private int Check(long offset, int length)
    {
        if (offset < 0 || offset + length > _data.Length) throw new InvalidDataException("The TIFF data is truncated.");
        return (int)offset;
    }

    private sealed record Entry(int Tag, int Type, uint Count, long ValueOffset);

    private static int TypeSize(int type) => type switch { 1 or 2 or 6 or 7 => 1, 3 or 8 => 2, 4 or 9 or 11 => 4, 5 or 10 or 12 => 8, _ => 0 };

    /// <summary>The integer values of a BYTE/SHORT/LONG entry (capped, so a hostile count cannot allocate gigabytes).</summary>
    private uint[] Values(Entry e)
    {
        if (e.Count > 1 << 22) throw new InvalidDataException("The TIFF has an oversized tag.");
        var size = TypeSize(e.Type);
        var result = new uint[e.Count];
        for (var i = 0; i < result.Length; i++)
        {
            var at = e.ValueOffset + (long)i * size;
            result[i] = e.Type switch
            {
                1 or 7 => _data[Check(at, 1)],
                3 => U16(at),
                4 => U32(at),
                _ => throw new InvalidDataException("The TIFF tag has an unexpected type.")
            };
        }
        return result;
    }

    private RgbaImage DecodeCore(long maxPixels, out int orientation)
    {
        var ifd = U32(4);
        var count = U16(ifd);
        var tags = new Dictionary<int, Entry>();
        for (var i = 0; i < count; i++)
        {
            var at = ifd + 2 + i * 12L;
            var tag = U16(at);
            var type = U16(at + 2);
            var n = U32(at + 4);
            var size = TypeSize(type);
            if (size == 0) continue;
            var inline = (long)size * n <= 4;
            tags[tag] = new Entry(tag, type, n, inline ? at + 8 : U32(at + 8));
        }

        uint One(int tag, uint fallback) => tags.TryGetValue(tag, out var e) && e.Count > 0 ? Values(e)[0] : fallback;

        var width = (int)One(256, 0);
        var height = (int)One(257, 0);
        if (width <= 0 || height <= 0) throw new InvalidDataException("The TIFF has invalid dimensions.");
        if ((long)width * height > maxPixels) throw new InvalidDataException($"The image has {(long)width * height} pixels, over the {maxPixels} limit.");

        var compression = (int)One(259, 1);
        var photometric = (int)One(262, 1);
        var spp = (int)One(277, 1);
        var rowsPerStrip = (int)Math.Min(One(278, (uint)height), (uint)height);
        var planar = One(284, 1);
        var predictor = (int)One(317, 1);
        orientation = (int)One(274, 1);
        if (orientation is < 1 or > 8) orientation = 1;

        var bitsList = tags.TryGetValue(258, out var bitsEntry) ? Values(bitsEntry) : [1];
        var bits = (int)bitsList[0];
        if (bitsList.Any(b => b != bits)) throw new NotSupportedException("TIFFs with mixed sample sizes are not supported.");
        if (bits is not (1 or 2 or 4 or 8 or 16)) throw new NotSupportedException($"{bits}-bit TIFF samples are not supported.");
        if (tags.ContainsKey(322) || tags.ContainsKey(323)) throw new NotSupportedException("Tiled TIFFs are not supported.");
        if (planar != 1) throw new NotSupportedException("Planar-separate TIFFs are not supported.");
        if (One(339, 1) != 1) throw new NotSupportedException("Only unsigned-integer TIFF samples are supported.");
        if (One(266, 1) != 1) throw new NotSupportedException("Reversed bit order (FillOrder 2) is not supported.");
        if (predictor is not (1 or 2)) throw new NotSupportedException("This TIFF predictor is not supported.");
        if (compression is not (1 or 5 or 8 or 32946 or 32773)) throw new NotSupportedException("This TIFF compression method (JPEG, CCITT, ...) is not supported.");
        if (spp is < 1 or > 5) throw new InvalidDataException("The TIFF has an invalid sample count.");
        if (predictor == 2 && bits is not (8 or 16)) throw new NotSupportedException("The TIFF predictor is only supported for 8 and 16-bit samples.");

        var colourSamples = photometric switch { 0 or 1 or 3 => 1, 2 => 3, 5 => 4, _ => throw new NotSupportedException("This TIFF colour model (Lab, YCbCr, ...) is not supported.") };
        if (spp < colourSamples) throw new InvalidDataException("The TIFF has too few samples per pixel.");
        if (photometric == 3 && bits > 8) throw new InvalidDataException("A palette TIFF cannot be 16-bit.");

        byte[]? palette = null;
        if (photometric == 3)
        {
            if (!tags.TryGetValue(320, out var mapEntry)) throw new InvalidDataException("The TIFF has no colour map.");
            var map = Values(mapEntry);
            var entries = 1 << bits;
            if (map.Length < entries * 3) throw new InvalidDataException("The TIFF colour map is too short.");
            palette = new byte[entries * 3];
            for (var i = 0; i < entries; i++)
                for (var c = 0; c < 3; c++) palette[i * 3 + c] = (byte)(map[c * entries + i] >> 8);
        }

        var extra = tags.TryGetValue(338, out var extraEntry) ? Values(extraEntry) : [];
        var alphaSample = spp > colourSamples && extra.Length > 0 && extra[0] is 1 or 2 ? colourSamples : -1;
        var premultiplied = extra.Length > 0 && extra[0] == 1;

        var rowBytes = checked((int)(((long)width * spp * bits + 7) / 8));
        var raw = new byte[checked(rowBytes * height)];
        ReadStrips(tags, raw, rowBytes, height, rowsPerStrip, compression);
        if (predictor == 2) UndoPredictor(raw, rowBytes, height, width, spp, bits);
        if (bits == 16 && _little) NormaliseEndianness(raw); // from here on 16-bit samples are read big-endian

        var image = RgbaImage.Create(width, height);
        var pixels = image.Pixels;
        var max = (1 << bits) - 1;

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var o = (y * width + x) * 4;
                int Sample(int s) => ReadSample(raw, y * rowBytes, x * spp + s, bits);
                byte Scale(int v) => bits == 8 ? (byte)v : bits == 16 ? (byte)(v >> 8) : (byte)((v * 255 + max / 2) / max);

                switch (photometric)
                {
                    case 0 or 1:
                    {
                        var v = Scale(Sample(0));
                        if (photometric == 0) v = (byte)(255 - v);
                        pixels[o] = pixels[o + 1] = pixels[o + 2] = v;
                        break;
                    }
                    case 3:
                        Buffer.BlockCopy(palette!, Sample(0) * 3, pixels, o, 3);
                        break;
                    case 2:
                        pixels[o] = Scale(Sample(0)); pixels[o + 1] = Scale(Sample(1)); pixels[o + 2] = Scale(Sample(2));
                        break;
                    default: // CMYK, 0 = no ink
                    {
                        int k = 255 - Scale(Sample(3));
                        pixels[o] = (byte)((255 - Scale(Sample(0))) * k / 255);
                        pixels[o + 1] = (byte)((255 - Scale(Sample(1))) * k / 255);
                        pixels[o + 2] = (byte)((255 - Scale(Sample(2))) * k / 255);
                        break;
                    }
                }

                var alpha = alphaSample >= 0 ? Scale(Sample(alphaSample)) : (byte)255;
                if (premultiplied && alpha is > 0 and < 255)
                    for (var c = 0; c < 3; c++) pixels[o + c] = (byte)Math.Min(255, (pixels[o + c] * 255 + alpha / 2) / alpha);
                pixels[o + 3] = alpha;
            }
        }

        if (tags.TryGetValue(34675, out var icc) && icc.Count is > 0 and < 1 << 24)
        {
            var profile = _data.AsSpan(Check(icc.ValueOffset, (int)icc.Count), (int)icc.Count).ToArray();
            image.Metadata = new ImageMetadata(null, profile);
        }
        return image;
    }

    private static int ReadSample(byte[] raw, int rowStart, int index, int bits)
    {
        switch (bits)
        {
            case 8: return raw[rowStart + index];
            case 16: return raw[rowStart + index * 2] << 8 | raw[rowStart + index * 2 + 1]; // normalised to big-endian by ReadStrips
            default:
            {
                var bit = index * bits;
                return (raw[rowStart + (bit >> 3)] >> (8 - bits - (bit & 7))) & ((1 << bits) - 1);
            }
        }
    }

    private void ReadStrips(Dictionary<int, Entry> tags, byte[] raw, int rowBytes, int height, int rowsPerStrip, int compression)
    {
        if (!tags.TryGetValue(273, out var offsetsEntry)) throw new InvalidDataException("The TIFF has no strip offsets.");
        var offsets = Values(offsetsEntry);
        var counts = tags.TryGetValue(279, out var countsEntry) ? Values(countsEntry) : null;
        var stripCount = (height + rowsPerStrip - 1) / rowsPerStrip;
        if (offsets.Length < stripCount) throw new InvalidDataException("The TIFF is missing strips.");

        for (var s = 0; s < stripCount; s++)
        {
            var rows = Math.Min(rowsPerStrip, height - s * rowsPerStrip);
            var expected = rows * rowBytes;
            var start = (long)offsets[s];
            var length = counts != null && s < counts.Length ? counts[s] : (uint)Math.Max(0, _data.Length - start);
            if (start < 0 || start > _data.Length) throw new InvalidDataException("The TIFF strip lies outside the file.");
            length = (uint)Math.Min(length, _data.Length - start);
            var source = _data.AsSpan((int)start, (int)length);

            var strip = compression switch
            {
                1 => source.ToArray(),
                32773 => PackBits(source, expected),
                5 => TiffLzw.Decode(source, expected),
                _ => Inflate(source, expected)
            };
            if (strip.Length < expected) throw new InvalidDataException("A TIFF strip is shorter than its dimensions require.");
            Buffer.BlockCopy(strip, 0, raw, s * rowsPerStrip * rowBytes, expected);
        }
    }

    private static byte[] Inflate(ReadOnlySpan<byte> source, int expected)
    {
        var result = new byte[expected];
        using var z = new ZLibStream(new MemoryStream(source.ToArray()), CompressionMode.Decompress);
        z.ReadExactly(result);
        return result;
    }

    private static byte[] PackBits(ReadOnlySpan<byte> source, int expected)
    {
        var result = new byte[expected];
        int i = 0, o = 0;
        while (i < source.Length && o < expected)
        {
            var n = (sbyte)source[i++];
            if (n >= 0)
            {
                var run = Math.Min(n + 1, Math.Min(source.Length - i, expected - o));
                source.Slice(i, run).CopyTo(result.AsSpan(o));
                i += n + 1;
                o += run;
            }
            else if (n != -128 && i < source.Length)
            {
                var run = Math.Min(1 - n, expected - o);
                result.AsSpan(o, run).Fill(source[i++]);
                o += run;
            }
        }
        return result;
    }

    /// <summary>Predictor 2: each sample is stored as the difference from the one to its left.</summary>
    private void UndoPredictor(byte[] raw, int rowBytes, int height, int width, int spp, int bits)
    {
        for (var y = 0; y < height; y++)
        {
            var row = y * rowBytes;
            if (bits == 8)
            {
                for (var i = spp; i < width * spp; i++) raw[row + i] += raw[row + i - spp];
            }
            else
            {
                for (var i = spp; i < width * spp; i++)
                {
                    var at = row + i * 2;
                    var prev = _little ? raw[at - spp * 2] | raw[at - spp * 2 + 1] << 8 : raw[at - spp * 2] << 8 | raw[at - spp * 2 + 1];
                    var cur = _little ? raw[at] | raw[at + 1] << 8 : raw[at] << 8 | raw[at + 1];
                    var sum = (cur + prev) & 0xFFFF;
                    if (_little) { raw[at] = (byte)sum; raw[at + 1] = (byte)(sum >> 8); }
                    else { raw[at] = (byte)(sum >> 8); raw[at + 1] = (byte)sum; }
                }
            }
        }
    }

    private static void NormaliseEndianness(byte[] raw)
    {
        for (var i = 0; i + 1 < raw.Length; i += 2) (raw[i], raw[i + 1]) = (raw[i + 1], raw[i]);
    }
}
