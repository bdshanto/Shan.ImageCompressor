using System.Buffers.Binary;

namespace Shan.ImageCompressor.Jpeg;

internal static class ExifOrientation
{
    /// <summary>
    /// A copy of a raw EXIF TIFF block with its orientation tag set to 1 ("upright"), for carrying EXIF to an image whose pixels have
    /// already been rotated. Returns null when the block is not readable EXIF.
    /// </summary>
    public static byte[]? Normalize(byte[] tiff)
    {
        if (tiff.Length < 8) return null;
        bool little;
        if (tiff[0] == (byte)'I' && tiff[1] == (byte)'I') little = true;
        else if (tiff[0] == (byte)'M' && tiff[1] == (byte)'M') little = false;
        else return null;

        ushort U16(int o) => little ? BinaryPrimitives.ReadUInt16LittleEndian(tiff.AsSpan(o)) : BinaryPrimitives.ReadUInt16BigEndian(tiff.AsSpan(o));
        var copy = (byte[])tiff.Clone();
        var ifd = little ? BinaryPrimitives.ReadUInt32LittleEndian(tiff.AsSpan(4)) : BinaryPrimitives.ReadUInt32BigEndian(tiff.AsSpan(4));
        if (ifd > tiff.Length - 2) return copy;

        var entries = U16((int)ifd);
        for (var i = 0; i < entries; i++)
        {
            var entry = (int)ifd + 2 + i * 12;
            if (entry + 12 > tiff.Length) break;
            if (U16(entry) != 0x0112) continue;
            if (little) BinaryPrimitives.WriteUInt16LittleEndian(copy.AsSpan(entry + 8), 1);
            else BinaryPrimitives.WriteUInt16BigEndian(copy.AsSpan(entry + 8), 1);
            break;
        }
        return copy;
    }

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
