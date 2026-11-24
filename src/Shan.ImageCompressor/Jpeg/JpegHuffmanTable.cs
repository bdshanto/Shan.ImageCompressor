namespace Shan.ImageCompressor.Jpeg;

/// <summary>Canonical Huffman decode tables built from a DHT segment's code counts (ITU T.81 Annex C / F.2.2.3).</summary>
internal sealed class JpegHuffmanTable
{
    public int[] MaxCode { get; } = new int[17];
    public int[] MinCode { get; } = new int[17];
    public int[] ValuePointer { get; } = new int[17];
    public byte[] Values { get; }

    public JpegHuffmanTable(ReadOnlySpan<byte> counts, byte[] values)
    {
        Values = values;
        var code = 0;
        var k = 0;
        for (var length = 1; length <= 16; length++)
        {
            var n = counts[length - 1];
            ValuePointer[length] = k;
            MinCode[length] = code;
            code += n;
            k += n;
            MaxCode[length] = n > 0 ? code - 1 : -1;
            code <<= 1;
        }
    }
}
