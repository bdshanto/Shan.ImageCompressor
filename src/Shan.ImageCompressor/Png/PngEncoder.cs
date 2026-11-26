using System.Buffers.Binary;
using System.IO.Compression;

namespace Shan.ImageCompressor.Png;

/// <summary>Writes an 8-bit PNG: RGB when the image is fully opaque, RGBA otherwise, each scanline using its cheapest filter.</summary>
internal static class PngEncoder
{
    // hasAlpha: whether the image has real transparency, when the caller already knows; scanned for otherwise.
    public static MemoryStream Encode(RgbaImage image, ImageMetadata? metadata = null, bool? hasAlpha = null)
    {
        var alpha = hasAlpha ?? image.HasTransparency();
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
        using (var z = new ZLibStream(idat, ZLibLevel.Smallest, leaveOpen: true))
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
        if (metadata?.IccProfile is { Length: > 0 } icc)
        {
            using var profile = new MemoryStream();
            profile.Write("ICC profile\0\0"u8); // the profile's name, a NUL, then compression method 0
            using (var z = new ZLibStream(profile, ZLibLevel.Smallest, leaveOpen: true)) z.Write(icc);
            WriteChunk(output, "iCCP"u8, profile.GetBuffer().AsSpan(0, (int)profile.Length));
        }
        if (metadata?.Exif is { Length: > 0 } exif) WriteChunk(output, "eXIf"u8, exif);
        WriteChunk(output, "IDAT"u8, idat.GetBuffer().AsSpan(0, (int)idat.Length));
        WriteChunk(output, "IEND"u8, []);
        output.Position = 0;
        return output;
    }

    private static void ApplyFilter(int filter, byte[] row, byte[] prev, byte[] output, int bpp)
    {
        // One loop per filter rather than a switch per byte; the first bpp bytes have no left neighbour (a = c = 0).
        var lead = Math.Min(bpp, row.Length);
        switch (filter)
        {
            case 0:
                Buffer.BlockCopy(row, 0, output, 0, row.Length);
                break;
            case 1:
                Buffer.BlockCopy(row, 0, output, 0, lead);
                for (var i = lead; i < row.Length; i++) output[i] = (byte)(row[i] - row[i - bpp]);
                break;
            case 2:
                for (var i = 0; i < row.Length; i++) output[i] = (byte)(row[i] - prev[i]);
                break;
            case 3:
                for (var i = 0; i < lead; i++) output[i] = (byte)(row[i] - (prev[i] >> 1));
                for (var i = lead; i < row.Length; i++) output[i] = (byte)(row[i] - ((row[i - bpp] + prev[i]) >> 1));
                break;
            default:
                for (var i = 0; i < lead; i++) output[i] = (byte)(row[i] - Paeth(0, prev[i], 0));
                for (var i = lead; i < row.Length; i++) output[i] = (byte)(row[i] - Paeth(row[i - bpp], prev[i], prev[i - bpp]));
                break;
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
