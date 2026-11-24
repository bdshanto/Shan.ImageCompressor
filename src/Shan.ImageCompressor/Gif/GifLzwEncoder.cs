namespace Shan.ImageCompressor.Gif;

/// <summary>GIF's variable-width LZW compressor: writes one image's data as length-prefixed sub-blocks, ending with the zero block.</summary>
internal static class GifLzwEncoder
{
    public static void Write(Stream output, ReadOnlySpan<byte> pixels, int minCodeSize)
    {
        output.WriteByte((byte)minCodeSize);
        var writer = new SubBlockWriter(output);

        var clear = 1 << minCodeSize;
        var end = clear + 1;
        var firstChild = new short[4096]; // dictionary as a tree: each entry's first child and next sibling, matched on the appended pixel
        var sibling = new short[4096];
        var suffix = new byte[4096];

        var codeSize = minCodeSize + 1;
        var next = end + 1;
        var emitted = 0; // codes written since the last clear
        Array.Fill(firstChild, (short)-1);
        writer.Write(clear, codeSize);

        var current = (int)pixels[0];
        for (var i = 1; i < pixels.Length; i++)
        {
            var value = pixels[i];
            var child = firstChild[current];
            while (child != -1 && suffix[child] != value) child = sibling[child];
            if (child != -1) { current = child; continue; }

            writer.Write(current, codeSize);
            emitted++;
            suffix[next] = value;
            sibling[next] = firstChild[current];
            firstChild[current] = (short)next;
            next++;
            if (next > 1 << codeSize && codeSize < 12) codeSize++;

            if (next == 4096) // dictionary full: start over
            {
                writer.Write(clear, codeSize);
                Array.Fill(firstChild, (short)-1);
                codeSize = minCodeSize + 1;
                next = end + 1;
                emitted = 0;
            }
            current = value;
        }

        writer.Write(current, codeSize);
        emitted++;
        // a decoder adds an entry for this last code too (unless it is the first since a clear), which may widen the end code
        if (emitted > 1 && next < 4096)
        {
            next++;
            if (next > 1 << codeSize && codeSize < 12) codeSize++;
        }
        writer.Write(end, codeSize);
        writer.Finish();
    }

    private sealed class SubBlockWriter(Stream output)
    {
        private readonly byte[] _block = new byte[255];
        private int _length;
        private int _bits;
        private int _bitCount;

        public void Write(int code, int size)
        {
            _bits |= code << _bitCount;
            _bitCount += size;
            while (_bitCount >= 8)
            {
                Push((byte)_bits);
                _bits >>= 8;
                _bitCount -= 8;
            }
        }

        private void Push(byte value)
        {
            _block[_length++] = value;
            if (_length == 255) Flush();
        }

        private void Flush()
        {
            if (_length == 0) return;
            output.WriteByte((byte)_length);
            output.Write(_block, 0, _length);
            _length = 0;
        }

        public void Finish()
        {
            if (_bitCount > 0) Push((byte)_bits);
            Flush();
            output.WriteByte(0);
        }
    }
}
