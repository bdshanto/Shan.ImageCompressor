using System.Buffers.Binary;

namespace Shan.ImageCompressor.Color;

/// <summary>
/// Converts pixels from an embedded RGB matrix/TRC ICC profile (the kind Adobe RGB, Display P3, ProPhoto and most camera/monitor
/// profiles are) to sRGB, relative colorimetric. Profiles built from lookup tables (CMYK, some printer/scanner RGB) are not handled.
/// </summary>
internal sealed class SrgbConverter
{
    // XYZ (D50) -> linear sRGB, Bradford-adapted (Lindbloom).
    private static readonly double[] XyzToSrgb =
    [
         3.1338561, -1.6168667, -0.4906146,
        -0.9787684,  1.9161415,  0.0334540,
         0.0719453, -0.2289914,  1.4052427
    ];

    private const int EncodeSize = 8192;
    private static readonly byte[] Encode = BuildEncodeTable();

    private readonly float[][] _linear; // per channel: 8-bit value -> linear light
    private readonly float[] _matrix;   // profile RGB (linear) -> linear sRGB, row-major 3x3

    private SrgbConverter(float[][] linear, float[] matrix)
    {
        _linear = linear;
        _matrix = matrix;
    }

    /// <summary>The ICC colour space signature ('RGB ', 'GRAY', 'CMYK', ...) from the profile header, or null when too short.</summary>
    public static string? ColorSpace(byte[] profile) => profile.Length >= 20 ? System.Text.Encoding.ASCII.GetString(profile, 16, 4) : null;

    /// <summary>A converter for the profile, or null when it is not an RGB matrix/TRC profile, is unreadable, or is already (effectively) sRGB.</summary>
    public static SrgbConverter? TryCreate(byte[] profile)
    {
        try { return Create(profile); }
        catch (Exception e) when (e is IndexOutOfRangeException or ArgumentException or OverflowException or InvalidDataException) { return null; }
    }

    private static SrgbConverter? Create(byte[] p)
    {
        if (p.Length < 132 || ColorSpace(p) != "RGB " || System.Text.Encoding.ASCII.GetString(p, 20, 4) != "XYZ ") return null;

        var tags = new Dictionary<string, (int Offset, int Size)>();
        var count = (int)BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(128));
        if (count is < 1 or > 200) return null;
        for (var i = 0; i < count; i++)
        {
            var at = 132 + i * 12;
            var offset = (int)BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(at + 4));
            var size = (int)BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(at + 8));
            if (offset < 0 || size < 8 || offset + (long)size > p.Length) continue;
            tags[System.Text.Encoding.ASCII.GetString(p, at, 4)] = (offset, size);
        }
        foreach (var required in new[] { "rXYZ", "gXYZ", "bXYZ", "rTRC", "gTRC", "bTRC" })
            if (!tags.ContainsKey(required)) return null;

        var columns = new[] { "rXYZ", "gXYZ", "bXYZ" }.Select(t => ReadXyz(p, tags[t].Offset)).ToArray();
        var linear = new[] { "rTRC", "gTRC", "bTRC" }.Select(t => BuildLinearTable(p, tags[t].Offset, tags[t].Size)).ToArray();

        // profile linear RGB -> XYZ (columns are the primaries) -> linear sRGB
        var matrix = new float[9];
        for (var row = 0; row < 3; row++)
            for (var col = 0; col < 3; col++)
            {
                double sum = 0;
                for (var k = 0; k < 3; k++) sum += XyzToSrgb[row * 3 + k] * columns[col][k];
                matrix[row * 3 + col] = (float)sum;
            }

        var converter = new SrgbConverter(linear, matrix);
        return converter.IsIdentity() ? null : converter;
    }

    /// <summary>Whether the profile already maps (nearly) every colour to itself, i.e. it is sRGB or a close relative.</summary>
    private bool IsIdentity()
    {
        foreach (var v in new[] { 0, 32, 96, 160, 224, 255 })
        {
            foreach (var (r, g, b) in new[] { (v, v, v), (v, 0, 0), (0, v, 0), (0, 0, v), (v, 255 - v, 128) })
            {
                Span<byte> px = [(byte)r, (byte)g, (byte)b, 255];
                Convert(px);
                if (Math.Abs(px[0] - r) > 1 || Math.Abs(px[1] - g) > 1 || Math.Abs(px[2] - b) > 1) return false;
            }
        }
        return true;
    }

    /// <summary>Converts <paramref name="pixels"/> (RGBA, 4 bytes per pixel) in place.</summary>
    public void Apply(byte[] pixels)
    {
        var span = pixels.AsSpan();
        for (var i = 0; i + 3 < span.Length; i += 4) Convert(span.Slice(i, 4));
    }

    private void Convert(Span<byte> px)
    {
        var r = _linear[0][px[0]];
        var g = _linear[1][px[1]];
        var b = _linear[2][px[2]];
        px[0] = Quantise(_matrix[0] * r + _matrix[1] * g + _matrix[2] * b);
        px[1] = Quantise(_matrix[3] * r + _matrix[4] * g + _matrix[5] * b);
        px[2] = Quantise(_matrix[6] * r + _matrix[7] * g + _matrix[8] * b);
    }

    private static byte Quantise(float linear)
    {
        if (linear <= 0) return 0;
        if (linear >= 1) return 255;
        return Encode[(int)(linear * (EncodeSize - 1) + 0.5f)];
    }

    private static byte[] BuildEncodeTable()
    {
        var table = new byte[EncodeSize];
        for (var i = 0; i < EncodeSize; i++)
        {
            var c = i / (double)(EncodeSize - 1);
            var v = c <= 0.0031308 ? 12.92 * c : 1.055 * Math.Pow(c, 1 / 2.4) - 0.055;
            table[i] = (byte)Math.Clamp((int)Math.Round(v * 255), 0, 255);
        }
        return table;
    }

    private static double[] ReadXyz(byte[] p, int offset)
    {
        if (!p.AsSpan(offset, 4).SequenceEqual("XYZ "u8)) throw new InvalidDataException("Bad XYZ tag.");
        return [S15(p, offset + 8), S15(p, offset + 12), S15(p, offset + 16)];
    }

    private static double S15(byte[] p, int at) => BinaryPrimitives.ReadInt32BigEndian(p.AsSpan(at)) / 65536.0;

    /// <summary>Tone-response curve ('curv' or 'para') as 256 linear-light values for 8-bit inputs.</summary>
    private static float[] BuildLinearTable(byte[] p, int offset, int size)
    {
        var table = new float[256];
        var type = System.Text.Encoding.ASCII.GetString(p, offset, 4);

        if (type == "curv")
        {
            var n = (int)BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(offset + 8));
            if (n < 0 || 12 + (long)n * 2 > size) throw new InvalidDataException("Bad curve tag.");
            for (var i = 0; i < 256; i++)
            {
                var x = i / 255.0;
                double y;
                if (n == 0) y = x;
                else if (n == 1) y = Math.Pow(x, BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(offset + 12)) / 256.0);
                else
                {
                    var position = x * (n - 1);
                    var lo = (int)Math.Floor(position);
                    var hi = Math.Min(n - 1, lo + 1);
                    var a = BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(offset + 12 + lo * 2)) / 65535.0;
                    var b = BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(offset + 12 + hi * 2)) / 65535.0;
                    y = a + (b - a) * (position - lo);
                }
                table[i] = (float)y;
            }
            return table;
        }

        if (type == "para")
        {
            var function = BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(offset + 8));
            var counts = new[] { 1, 3, 4, 5, 7 };
            if (function > 4 || 12 + counts[function] * 4 > size) throw new InvalidDataException("Bad parametric curve tag.");
            var v = new double[7];
            for (var i = 0; i < counts[function]; i++) v[i] = S15(p, offset + 12 + i * 4);
            double g = v[0], a = v[1], b = v[2], c = v[3], d = v[4], e = v[5], f = v[6];

            for (var i = 0; i < 256; i++)
            {
                var x = i / 255.0;
                double y = function switch
                {
                    0 => Math.Pow(x, g),
                    1 => x >= -b / a ? Math.Pow(a * x + b, g) : 0,
                    2 => x >= -b / a ? Math.Pow(a * x + b, g) + c : c,
                    3 => x >= d ? Math.Pow(a * x + b, g) : c * x,
                    _ => x >= d ? Math.Pow(a * x + b, g) + e : c * x + f
                };
                table[i] = (float)Math.Clamp(double.IsNaN(y) ? 0 : y, 0, 1);
            }
            return table;
        }

        throw new InvalidDataException("Unknown tone curve type.");
    }
}
