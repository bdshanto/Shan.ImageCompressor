using System.Buffers.Binary;

namespace Shan.ImageCompressor.Png;

internal static class Crc32
{
    // Slicing-by-8: Tables[0] is the classic byte table; Tables[k] advances a byte through k further zero bytes, so eight bytes fold in per step.
    private static readonly uint[][] Tables = BuildTables();

    private static uint[][] BuildTables()
    {
        var tables = new uint[8][];
        tables[0] = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            tables[0][n] = c;
        }
        for (var t = 1; t < 8; t++)
        {
            tables[t] = new uint[256];
            for (var n = 0; n < 256; n++)
                tables[t][n] = (tables[t - 1][n] >> 8) ^ tables[0][tables[t - 1][n] & 0xFF];
        }
        return tables;
    }

    /// <summary>Continues a running CRC; start with <c>0xFFFFFFFF</c> and finish by inverting.</summary>
    public static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        var t0 = Tables[0];
        var t1 = Tables[1];
        var t2 = Tables[2];
        var t3 = Tables[3];
        var t4 = Tables[4];
        var t5 = Tables[5];
        var t6 = Tables[6];
        var t7 = Tables[7];

        while (data.Length >= 8)
        {
            var lo = BinaryPrimitives.ReadUInt32LittleEndian(data) ^ crc;
            var hi = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
            crc = t7[lo & 0xFF] ^ t6[(lo >> 8) & 0xFF] ^ t5[(lo >> 16) & 0xFF] ^ t4[lo >> 24]
                ^ t3[hi & 0xFF] ^ t2[(hi >> 8) & 0xFF] ^ t1[(hi >> 16) & 0xFF] ^ t0[hi >> 24];
            data = data[8..];
        }
        foreach (var b in data)
            crc = t0[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }
}
