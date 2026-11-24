using System.Buffers.Binary;
using System.IO.Compression;

namespace Shan.ImageCompressor.Png;

/// <summary>Decodes every standard PNG colour type and bit depth, including Adam7 interlacing and tRNS transparency, to 8-bit RGBA.</summary>
internal sealed class PngDecoder
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

    private static readonly int[] PassX0 = [0, 4, 0, 2, 0, 1, 0];
    private static readonly int[] PassY0 = [0, 0, 4, 0, 2, 0, 1];
    private static readonly int[] PassDx = [8, 8, 4, 4, 2, 2, 1];
    private static readonly int[] PassDy = [8, 8, 8, 4, 4, 2, 2];

    private int _width;
    private int _height;
    private int _depth;
    private int _colorType;
    private bool _interlaced;
    private int _channels;
    private int _bitsPerPixel;
    private byte[]? _palette;
    private byte[]? _paletteAlpha;
    private int _transparentGray = -1;
    private int[]? _transparentRgb;

    public static RgbaImage Decode(byte[] data, long maxPixels)
    {
        try { return new PngDecoder().DecodeCore(data, maxPixels); }
        catch (Exception e) when (e is IndexOutOfRangeException or ArgumentException or OverflowException or EndOfStreamException)
        {
            throw new InvalidDataException("The PNG data is corrupt.", e);
        }
    }

    private RgbaImage DecodeCore(byte[] data, long maxPixels)
    {
        if (data.Length < 8 || !data.AsSpan(0, 8).SequenceEqual(Signature)) throw new InvalidDataException("Not a PNG.");

        var pos = 8;
        var haveHeader = false;
        byte[]? transparency = null;
        using var idat = new MemoryStream();

        while (pos + 8 <= data.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(pos));
            var type = data.AsSpan(pos + 4, 4);
            pos += 8;
            if (length < 0 || pos + length > data.Length) throw new InvalidDataException("The PNG data is truncated.");
            var body = data.AsSpan(pos, length);

            if (type.SequenceEqual("IHDR"u8)) { ReadHeader(body, maxPixels); haveHeader = true; }
            else if (type.SequenceEqual("PLTE"u8)) _palette = body.ToArray();
            else if (type.SequenceEqual("tRNS"u8)) transparency = body.ToArray();
            else if (type.SequenceEqual("IDAT"u8)) idat.Write(body);
            else if (type.SequenceEqual("IEND"u8)) break;

            pos += length + 4; // the chunk's CRC is not verified: zlib's own checksum catches a damaged image stream
        }

        if (!haveHeader || idat.Length == 0) throw new InvalidDataException("The PNG has no image data.");
        if (_colorType == 3 && (_palette is null || _palette.Length == 0 || _palette.Length % 3 != 0)) throw new InvalidDataException("The PNG is missing its palette.");
        if (transparency is not null) ReadTransparency(transparency);

        var raw = new byte[ExpectedRawSize()];
        idat.Position = 0;
        using (var z = new ZLibStream(idat, CompressionMode.Decompress))
            z.ReadExactly(raw);

        var image = RgbaImage.Create(_width, _height);
        if (!_interlaced) Unfilter(raw, 0, image, 0, 0, 1, 1, _width, _height);
        else
        {
            var offset = 0;
            for (var pass = 0; pass < 7; pass++)
            {
                var pw = (_width - PassX0[pass] + PassDx[pass] - 1) / PassDx[pass];
                var ph = (_height - PassY0[pass] + PassDy[pass] - 1) / PassDy[pass];
                if (pw <= 0 || ph <= 0) continue;
                offset = Unfilter(raw, offset, image, PassX0[pass], PassY0[pass], PassDx[pass], PassDy[pass], pw, ph);
            }
        }
        return image;
    }

    private void ReadHeader(ReadOnlySpan<byte> body, long maxPixels)
    {
        if (body.Length < 13) throw new InvalidDataException("The PNG header is truncated.");
        var w = BinaryPrimitives.ReadUInt32BigEndian(body);
        var h = BinaryPrimitives.ReadUInt32BigEndian(body[4..]);
        if (w == 0 || h == 0 || w > int.MaxValue || h > int.MaxValue) throw new InvalidDataException("The PNG has invalid dimensions.");
        if ((long)w * h > maxPixels) throw new InvalidDataException($"The image has {(long)w * h} pixels, over the {maxPixels} limit.");

        _width = (int)w;
        _height = (int)h;
        _depth = body[8];
        _colorType = body[9];
        if (body[10] != 0 || body[11] != 0 || body[12] > 1) throw new InvalidDataException("The PNG uses an unknown compression, filter or interlace method.");
        _interlaced = body[12] == 1;

        var valid = _colorType switch
        {
            0 => _depth is 1 or 2 or 4 or 8 or 16,
            2 or 4 or 6 => _depth is 8 or 16,
            3 => _depth is 1 or 2 or 4 or 8,
            _ => false
        };
        if (!valid) throw new InvalidDataException("The PNG has an invalid colour type / bit depth combination.");

        _channels = _colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, _ => 4 };
        _bitsPerPixel = _channels * _depth;
    }

    private void ReadTransparency(byte[] t)
    {
        switch (_colorType)
        {
            case 0 when t.Length >= 2: _transparentGray = BinaryPrimitives.ReadUInt16BigEndian(t); break;
            case 2 when t.Length >= 6:
                _transparentRgb = [BinaryPrimitives.ReadUInt16BigEndian(t), BinaryPrimitives.ReadUInt16BigEndian(t.AsSpan(2)), BinaryPrimitives.ReadUInt16BigEndian(t.AsSpan(4))];
                break;
            case 3: _paletteAlpha = t; break;
        }
    }

    private long RowBytes(int pixels) => ((long)pixels * _bitsPerPixel + 7) / 8;

    private int ExpectedRawSize()
    {
        long total = 0;
        if (!_interlaced) total = (RowBytes(_width) + 1) * _height;
        else
            for (var pass = 0; pass < 7; pass++)
            {
                var pw = (_width - PassX0[pass] + PassDx[pass] - 1) / PassDx[pass];
                var ph = (_height - PassY0[pass] + PassDy[pass] - 1) / PassDy[pass];
                if (pw > 0 && ph > 0) total += (RowBytes(pw) + 1) * ph;
            }
        return checked((int)total);
    }

    /// <summary>Unfilters one (sub)image of <paramref name="pw"/>×<paramref name="ph"/> pixels and scatters it into the output. Returns the new raw offset.</summary>
    private int Unfilter(byte[] raw, int offset, RgbaImage image, int x0, int y0, int dx, int dy, int pw, int ph)
    {
        var stride = (int)RowBytes(pw);
        var bpp = Math.Max(1, _bitsPerPixel / 8);
        var cur = new byte[stride];
        var prev = new byte[stride];

        for (var y = 0; y < ph; y++)
        {
            var filter = raw[offset++];
            Buffer.BlockCopy(raw, offset, cur, 0, stride);
            offset += stride;

            switch (filter)
            {
                case 0: break;
                case 1: for (var i = bpp; i < stride; i++) cur[i] += cur[i - bpp]; break;
                case 2: for (var i = 0; i < stride; i++) cur[i] += prev[i]; break;
                case 3:
                    for (var i = 0; i < stride; i++)
                        cur[i] += (byte)(((i >= bpp ? cur[i - bpp] : 0) + prev[i]) >> 1);
                    break;
                case 4:
                    for (var i = 0; i < stride; i++)
                        cur[i] += Paeth(i >= bpp ? cur[i - bpp] : 0, prev[i], i >= bpp ? prev[i - bpp] : 0);
                    break;
                default: throw new InvalidDataException("The PNG uses an unknown scanline filter.");
            }

            ConvertRow(cur, pw, image.Pixels, ((y0 + y * dy) * _width + x0) * 4, dx * 4);
            (cur, prev) = (prev, cur);
        }
        return offset;
    }

    private static byte Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);
        return (byte)(pa <= pb && pa <= pc ? a : pb <= pc ? b : c);
    }

    private int Sample(byte[] row, int index) // one sample of a single-channel row (gray / palette index)
    {
        switch (_depth)
        {
            case 8: return row[index];
            case 16: return (row[index * 2] << 8) | row[index * 2 + 1];
            default:
                var bit = index * _depth;
                var shift = 8 - _depth - (bit & 7);
                return (row[bit >> 3] >> shift) & ((1 << _depth) - 1);
        }
    }

    private byte To8(int sample) => _depth switch
    {
        16 => (byte)(sample >> 8),
        8 => (byte)sample,
        _ => (byte)(sample * 255 / ((1 << _depth) - 1))
    };

    private void ConvertRow(byte[] row, int count, byte[] output, int dst, int step)
    {
        for (var x = 0; x < count; x++, dst += step)
        {
            byte r, g, b, a = 255;
            switch (_colorType)
            {
                case 0:
                {
                    var s = Sample(row, x);
                    r = g = b = To8(s);
                    if (s == _transparentGray) a = 0;
                    break;
                }
                case 2:
                {
                    int rr, gg, bb;
                    if (_depth == 8) { rr = row[x * 3]; gg = row[x * 3 + 1]; bb = row[x * 3 + 2]; }
                    else
                    {
                        rr = (row[x * 6] << 8) | row[x * 6 + 1];
                        gg = (row[x * 6 + 2] << 8) | row[x * 6 + 3];
                        bb = (row[x * 6 + 4] << 8) | row[x * 6 + 5];
                    }
                    r = To8(rr); g = To8(gg); b = To8(bb);
                    if (_transparentRgb is { } t && rr == t[0] && gg == t[1] && bb == t[2]) a = 0;
                    break;
                }
                case 3:
                {
                    var index = Sample(row, x);
                    if (index * 3 + 2 >= _palette!.Length) throw new InvalidDataException("The PNG refers to a palette entry that does not exist.");
                    r = _palette[index * 3]; g = _palette[index * 3 + 1]; b = _palette[index * 3 + 2];
                    if (_paletteAlpha is not null && index < _paletteAlpha.Length) a = _paletteAlpha[index];
                    break;
                }
                case 4:
                    if (_depth == 8) { r = g = b = row[x * 2]; a = row[x * 2 + 1]; }
                    else { r = g = b = row[x * 4]; a = row[x * 4 + 2]; }
                    break;
                default:
                    if (_depth == 8) { r = row[x * 4]; g = row[x * 4 + 1]; b = row[x * 4 + 2]; a = row[x * 4 + 3]; }
                    else { r = row[x * 8]; g = row[x * 8 + 2]; b = row[x * 8 + 4]; a = row[x * 8 + 6]; }
                    break;
            }
            output[dst] = r; output[dst + 1] = g; output[dst + 2] = b; output[dst + 3] = a;
        }
    }
}
