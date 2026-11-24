namespace Shan.ImageCompressor.Jpeg;

/// <summary>Writes entropy-coded JPEG bits, inserting the mandatory 0x00 after every 0xFF data byte.</summary>
internal sealed class JpegBitWriter(Stream output)
{
    private uint _buffer;
    private int _count;

    public void Write(uint code, int bits)
    {
        if (bits == 0) return;
        _buffer = (_buffer << bits) | (code & ((1u << bits) - 1));
        _count += bits;
        while (_count >= 8)
        {
            var b = (byte)(_buffer >> (_count - 8));
            output.WriteByte(b);
            if (b == 0xFF) output.WriteByte(0);
            _count -= 8;
        }
        _buffer &= (1u << _count) - 1;
    }

    /// <summary>Pads the final partial byte with 1-bits, as the standard requires.</summary>
    public void Flush()
    {
        if (_count > 0) Write((1u << (8 - _count)) - 1, 8 - _count);
    }
}
