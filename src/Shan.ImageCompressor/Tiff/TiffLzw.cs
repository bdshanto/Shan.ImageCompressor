namespace Shan.ImageCompressor.Tiff;

/// <summary>TIFF's LZW variant: MSB-first codes of 9–12 bits, with the "early change" switch of code width (TIFF 6.0).</summary>
internal static class TiffLzw
{
    private const int Clear = 256;
    private const int End = 257;

    public static byte[] Decode(ReadOnlySpan<byte> input, int expected)
    {
        var output = new byte[expected];
        var prefix = new short[4096];
        var suffix = new byte[4096];
        var length = new int[4096];
        for (var i = 0; i < 256; i++) { suffix[i] = (byte)i; length[i] = 1; prefix[i] = -1; }

        var codeSize = 9;
        var next = 258;
        var previous = -1;
        var bits = 0;
        var bitCount = 0;
        var inputPos = 0;
        var written = 0;

        while (written < expected)
        {
            while (bitCount < codeSize)
            {
                if (inputPos >= input.Length) return output;
                bits = (bits << 8) | input[inputPos++];
                bitCount += 8;
            }
            var code = (bits >> (bitCount - codeSize)) & ((1 << codeSize) - 1);
            bitCount -= codeSize;
            bits &= (1 << bitCount) - 1;

            if (code == End) break;
            if (code == Clear)
            {
                codeSize = 9;
                next = 258;
                previous = -1;
                continue;
            }

            int entryLength;
            if (code < next && (code < 256 || code >= 258)) entryLength = length[code];
            else if (code == next && previous >= 0) entryLength = length[previous] + 1;
            else throw new InvalidDataException("The TIFF LZW data is corrupt.");

            // write the string for `code` (or previous + its own first byte, for the not-yet-defined code) from the back
            var start = written;
            var end = Math.Min(expected, start + entryLength);
            var pos = start + entryLength - 1;
            var current = code;
            if (code == next)
            {
                var first = FirstByte(previous, prefix, suffix);
                if (pos < expected) output[pos] = first;
                pos--;
                current = previous;
            }
            while (current >= 0 && pos >= start)
            {
                if (pos < expected) output[pos] = suffix[current];
                pos--;
                current = prefix[current];
            }
            written = end;

            if (previous >= 0 && next < 4096)
            {
                prefix[next] = (short)previous;
                suffix[next] = output[start]; // the first byte of the string just written
                length[next] = length[previous] + 1;
                next++;
                if (next == (1 << codeSize) - 1 && codeSize < 12) codeSize++; // early change
            }
            previous = code;
        }
        return output;
    }

    private static byte FirstByte(int code, short[] prefix, byte[] suffix)
    {
        while (prefix[code] >= 0) code = prefix[code];
        return suffix[code];
    }
}
