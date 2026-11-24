using System.Buffers.Binary;

namespace Shan.ImageCompressor.Jpeg;

internal static class ExifOrientation
{
    /// <summary>The EXIF orientation (1–8) from an APP1 segment body, or 1 when absent or unreadable.</summary>
    public static int Read(ReadOnlySpan<byte> app1)
    {
        if (app1.Length < 14 || !app1[..6].SequenceEqual("Exif\0\0"u8)) return 1;
        var tiff = app1[6..].ToArray();

        bool littleEndian;
        if (tiff[0] == (byte)'I' && tiff[1] == (byte)'I') littleEndian = true;
        else if (tiff[0] == (byte)'M' && tiff[1] == (byte)'M') littleEndian = false;
        else return 1;

        ushort U16(int offset) => littleEndian ? BinaryPrimitives.ReadUInt16LittleEndian(tiff.AsSpan(offset)) : BinaryPrimitives.ReadUInt16BigEndian(tiff.AsSpan(offset));
        uint U32(int offset) => littleEndian ? BinaryPrimitives.ReadUInt32LittleEndian(tiff.AsSpan(offset)) : BinaryPrimitives.ReadUInt32BigEndian(tiff.AsSpan(offset));

        var ifd = U32(4);
        if (ifd > tiff.Length - 2) return 1;
        var entries = U16((int)ifd);
        for (var i = 0; i < entries; i++)
        {
            var entry = (int)ifd + 2 + i * 12;
            if (entry + 12 > tiff.Length) break;
            if (U16(entry) != 0x0112) continue;
            var value = U16(entry + 8);
            return value is >= 1 and <= 8 ? value : 1;
        }
        return 1;
    }
}
