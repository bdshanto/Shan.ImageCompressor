namespace Shan.ImageCompressor.Jpeg;

/// <summary>Reads entropy-coded bits: removes 0xFF00 stuffing and feeds zeros once a marker (or the end of data) is reached.</summary>
internal sealed class JpegBitReader(byte[] data, int position)
{
    private int _bits;
    private int _count;

    public int Position { get; private set; } = position;

    public int ReadBit()
    {
        if (_count == 0)
        {
            _bits = NextByte();
            _count = 8;
        }
        _count--;
        return (_bits >> _count) & 1;
    }

    public int ReadBits(int n)
    {
        var v = 0;
        for (var i = 0; i < n; i++) v = (v << 1) | ReadBit();
        return v;
    }

    /// <summary>Reads <paramref name="n"/> bits and sign-extends per the JPEG "EXTEND" procedure.</summary>
    public int ReceiveExtend(int n)
    {
        if (n == 0) return 0;
        var v = ReadBits(n);
        return v < (1 << (n - 1)) ? v - (1 << n) + 1 : v;
    }

    public int Decode(JpegHuffmanTable table)
    {
        var code = 0;
        for (var length = 1; length <= 16; length++)
        {
            code = (code << 1) | ReadBit();
            if (code <= table.MaxCode[length])
                return table.Values[table.ValuePointer[length] + code - table.MinCode[length]];
        }
        throw new InvalidDataException("The JPEG contains an invalid Huffman code.");
    }

    /// <summary>Discards any partial byte and steps over a restart marker (RSTn) if one is next.</summary>
    public void Restart()
    {
        _count = 0;
        if (Position + 1 < data.Length && data[Position] == 0xFF && data[Position + 1] is >= 0xD0 and <= 0xD7)
            Position += 2;
    }

    private int NextByte()
    {
        if (Position >= data.Length) return 0;
        var b = data[Position];
        if (b != 0xFF)
        {
            Position++;
            return b;
        }
        if (Position + 1 < data.Length && data[Position + 1] == 0)
        {
            Position += 2;
            return 0xFF;
        }
        return 0; // a real marker: stay put so the caller can find it, and feed zeros meanwhile
    }
}
