using System.Buffers.Binary;
using System.IO.Compression;

namespace Shan.ImageCompressor.Png;

/// <summary>Writes an 8-bit PNG: RGB when the image is fully opaque, RGBA otherwise, each scanline using its cheapest filter.</summary>
internal static class PngEncoder
{
    public static MemoryStream Encode(RgbaImage image)
    {
        var alpha = image.HasTransparency();
        var channels = alpha ? 4 : 3;
        var stride = image.Width * channels;

        var raw = new byte[(stride + 1) * image.Height];
        var row = new byte[stride];
        var prev = new byte[stride];
        var candidate = new byte[stride];
        var best = new byte[stride];

        for (var y = 0; y < image.Height; y++)
        {
            var src = y * image.Width * 4;
            for (var x = 0; x < image.Width; x++)
            {
                row[x * channels] = image.Pixels[src + x * 4];
                row[x * channels + 1] = image.Pixels[src + x * 4 + 1];
                row[x * channels + 2] = image.Pixels[src + x * 4 + 2];
                if (alpha) row[x * channels + 3] = image.Pixels[src + x * 4 + 3];
            }

            var bestFilter = 0;
            var bestScore = long.MaxValue;
            for (var filter = 0; filter < 5; filter++)
            {
                ApplyFilter(filter, row, prev, candidate, channels);
                long score = 0;
                foreach (var b in candidate) score += Math.Abs((int)(sbyte)b);
                if (score < bestScore)
                {
                    bestScore = score;
                    bestFilter = filter;
                    (best, candidate) = (candidate, best);
                }
            }

            var dst = y * (stride + 1);
            raw[dst] = (byte)bestFilter;
            Buffer.BlockCopy(best, 0, raw, dst + 1, stride);
            Buffer.BlockCopy(row, 0, prev, 0, stride);
        }

        var idat = new MemoryStream();
        using (var z = new ZLibStream(idat, CompressionLevel.SmallestSize, leaveOpen: true))
            z.Write(raw);

        var output = new MemoryStream();
        output.Write([137, 80, 78, 71, 13, 10, 26, 10]);

        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)image.Width);
        BinaryPrimitives.WriteUInt32BigEndian(header[4..], (uint)image.Height);
        header[8] = 8;
        header[9] = alpha ? (byte)6 : (byte)2;
        header[10] = header[11] = header[12] = 0;

        WriteChunk(output, "IHDR"u8, header);
        WriteChunk(output, "IDAT"u8, idat.GetBuffer().AsSpan(0, (int)idat.Length));
        WriteChunk(output, "IEND"u8, []);
        output.Position = 0;
        return output;
    }

    private static void ApplyFilter(int filter, byte[] row, byte[] prev, byte[] output, int bpp)
    {
        for (var i = 0; i < row.Length; i++)
        {
            int a = i >= bpp ? row[i - bpp] : 0;
            int b = prev[i];
            int c = i >= bpp ? prev[i - bpp] : 0;
            output[i] = filter switch
            {
                0 => row[i],
                1 => (byte)(row[i] - a),
                2 => (byte)(row[i] - b),
                3 => (byte)(row[i] - ((a + b) >> 1)),
                _ => (byte)(row[i] - Paeth(a, b, c))
            };
        }
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static void WriteChunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> word = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(word, (uint)data.Length);
        output.Write(word);
        output.Write(type);
        output.Write(data);

        var crc = Crc32.Update(0xFFFFFFFFu, type);
        crc = Crc32.Update(crc, data) ^ 0xFFFFFFFFu;
        BinaryPrimitives.WriteUInt32BigEndian(word, crc);
        output.Write(word);
    }
}
