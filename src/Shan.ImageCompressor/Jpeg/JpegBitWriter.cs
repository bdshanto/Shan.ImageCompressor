namespace Shan.ImageCompressor.Jpeg;

/// <summary>Writes entropy-coded JPEG bits, inserting the mandatory 0x00 after every 0xFF data byte.</summary>
internal sealed class JpegBitWriter(Stream output)
{
    private readonly byte[] _bytes = new byte[8192]; // batches the stream writes; an entry can add two bytes, so flush while a pair still fits
    private int _length;
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
            _bytes[_length++] = b;
            if (b == 0xFF) _bytes[_length++] = 0;
            _count -= 8;
        }
        _buffer &= (1u << _count) - 1;
        if (_length > _bytes.Length - 16) Drain();
    }

    private void Drain()
    {
        output.Write(_bytes, 0, _length);
        _length = 0;
    }

    /// <summary>Pads the final partial byte with 1-bits, as the standard requires, and writes everything still buffered.</summary>
    public void Flush()
    {
        if (_count > 0) Write((1u << (8 - _count)) - 1, 8 - _count);
        Drain();
    }
}
