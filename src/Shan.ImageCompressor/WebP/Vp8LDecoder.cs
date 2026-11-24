namespace Shan.ImageCompressor.WebP;

/// <summary>Decodes a lossless WebP (the VP8L bitstream): prefix-coded ARGB with LZ77 back-references, a colour cache and four image transforms.</summary>
internal static class Vp8LDecoder
{
    private const int MaxCodeLength = 15;
    private const int NumLiteralCodes = 256, NumLengthCodes = 24, NumDistanceCodes = 40;

    // The 120 short-distance codes: (xOffset, yOffset) back from the current pixel.
    private static readonly (int X, int Y)[] DistanceMap =
    [
        (0, 1), (1, 0), (1, 1), (-1, 1), (0, 2), (2, 0), (1, 2),
        (-1, 2), (2, 1), (-2, 1), (2, 2), (-2, 2), (0, 3), (3, 0),
        (1, 3), (-1, 3), (3, 1), (-3, 1), (2, 3), (-2, 3), (3, 2),
        (-3, 2), (0, 4), (4, 0), (1, 4), (-1, 4), (4, 1), (-4, 1),
        (3, 3), (-3, 3), (2, 4), (-2, 4), (4, 2), (-4, 2), (0, 5),
        (3, 4), (-3, 4), (4, 3), (-4, 3), (5, 0), (1, 5), (-1, 5),
        (5, 1), (-5, 1), (2, 5), (-2, 5), (5, 2), (-5, 2), (4, 4),
        (-4, 4), (3, 5), (-3, 5), (5, 3), (-5, 3), (0, 6), (6, 0),
        (1, 6), (-1, 6), (6, 1), (-6, 1), (2, 6), (-2, 6), (6, 2),
        (-6, 2), (4, 5), (-4, 5), (5, 4), (-5, 4), (3, 6), (-3, 6),
        (6, 3), (-6, 3), (0, 7), (7, 0), (1, 7), (-1, 7), (5, 5),
        (-5, 5), (7, 1), (-7, 1), (4, 6), (-4, 6), (6, 4), (-6, 4),
        (2, 7), (-2, 7), (7, 2), (-7, 2), (3, 7), (-3, 7), (7, 3),
        (-7, 3), (5, 6), (-5, 6), (6, 5), (-6, 5), (8, 0), (4, 7),
        (-4, 7), (7, 4), (-7, 4), (8, 1), (8, 2), (6, 6), (-6, 6),
        (8, 3), (5, 7), (-5, 7), (7, 5), (-7, 5), (8, 4), (6, 7),
        (-6, 7), (7, 6), (-7, 6), (8, 5), (7, 7), (-7, 7), (8, 6),
        (8, 7)
    ];

    internal static int DistanceMapLength => DistanceMap.Length;

    private static readonly int[] CodeLengthOrder = [17, 18, 0, 1, 2, 3, 4, 5, 16, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15];

    public static RgbaImage Decode(ReadOnlySpan<byte> data, long maxPixels)
    {
        var bits = new BitReader(data);
        if (bits.Read(8) != 0x2F) throw new InvalidDataException("Not a lossless WebP stream.");
        var width = (int)bits.Read(14) + 1;
        var height = (int)bits.Read(14) + 1;
        var alphaUsed = bits.Read(1) != 0;
        if (bits.Read(3) != 0) throw new NotSupportedException("This lossless WebP version is not supported.");
        if ((long)width * height > maxPixels) throw new InvalidDataException($"The image has {(long)width * height} pixels, over the {maxPixels} limit.");

        var pixels = DecodeImage(bits, width, height, topLevel: true, out var transforms);
        var xsize = width;
        // transforms were read in order; undo them in reverse (a colour-indexing transform narrowed the width the later ones saw)
        for (var t = transforms.Count - 1; t >= 0; t--)
        {
            var transform = transforms[t];
            pixels = transform.Inverse(pixels, transform.Width, height);
            xsize = transform.OutputWidth;
        }

        var image = RgbaImage.Create(width, height);
        var rgba = image.Pixels;
        for (var i = 0; i < pixels.Length; i++)
        {
            var argb = pixels[i];
            rgba[i * 4] = (byte)(argb >> 16);
            rgba[i * 4 + 1] = (byte)(argb >> 8);
            rgba[i * 4 + 2] = (byte)argb;
            rgba[i * 4 + 3] = alphaUsed ? (byte)(argb >> 24) : (byte)255;
        }
        return image;
    }

    // ---------------------------------------------------------------------------------------------------------------------------------

    private sealed class Transform
    {
        public int Kind;
        public int Width;          // width of the image this transform was applied to
        public int OutputWidth;    // width after undoing it
        public int Bits;
        public uint[] Data = [];   // the transform's own sub-image (predictor modes, colour coefficients, or palette)
        public Func<uint[], int, int, uint[]> Inverse = (p, _, _) => p;
    }

    private static uint[] DecodeImage(BitReader bits, int width, int height, bool topLevel, out List<Transform> transforms)
    {
        transforms = [];
        var xsize = width;
        if (topLevel)
        {
            var seen = 0;
            while (bits.Read(1) != 0)
            {
                var kind = (int)bits.Read(2);
                if ((seen & 1 << kind) != 0) throw new InvalidDataException("A WebP transform is used twice.");
                seen |= 1 << kind;

                var transform = new Transform { Kind = kind, Width = xsize, OutputWidth = xsize };
                switch (kind)
                {
                    case 0 or 1: // predictor / cross-colour: one entry per block
                        transform.Bits = (int)bits.Read(3) + 2;
                        transform.Data = DecodeImage(bits, SubSize(xsize, transform.Bits), SubSize(height, transform.Bits), false, out _);
                        break;
                    case 3: // colour indexing
                        var size = (int)bits.Read(8) + 1;
                        var table = DecodeImage(bits, size, 1, false, out _);
                        for (var i = 1; i < table.Length; i++) table[i] = AddPixels(table[i], table[i - 1]); // stored as deltas
                        transform.Data = table;
                        transform.Bits = size <= 2 ? 3 : size <= 4 ? 2 : size <= 16 ? 1 : 0;
                        transform.OutputWidth = xsize;
                        xsize = SubSize(xsize, transform.Bits);
                        break;
                }
                var captured = transform;
                transform.Inverse = kind switch
                {
                    0 => (p, w, h) => InversePredictor(p, w, h, captured),
                    1 => (p, w, h) => InverseCrossColour(p, w, h, captured),
                    2 => (p, _, _) => InverseSubtractGreen(p),
                    _ => (p, w, h) => InverseColourIndexing(p, captured.OutputWidth, h, captured)
                };
                transforms.Add(transform);
            }
        }

        // colour cache
        var cacheBits = 0;
        if (bits.Read(1) != 0)
        {
            cacheBits = (int)bits.Read(4);
            if (cacheBits is < 1 or > 11) throw new InvalidDataException("The WebP colour cache size is invalid.");
        }

        // which prefix-code group applies where
        uint[]? meta = null;
        var metaBits = 0;
        var groupCount = 1;
        if (topLevel && bits.Read(1) != 0)
        {
            metaBits = (int)bits.Read(3) + 2;
            meta = DecodeImage(bits, SubSize(xsize, metaBits), SubSize(height, metaBits), false, out _);
            for (var i = 0; i < meta.Length; i++)
            {
                meta[i] = (meta[i] >> 8) & 0xFFFF;
                groupCount = Math.Max(groupCount, (int)meta[i] + 1);
            }
        }

        var cacheSize = cacheBits > 0 ? 1 << cacheBits : 0;
        var groups = new HuffmanCode[groupCount][];
        for (var g = 0; g < groupCount; g++)
        {
            groups[g] =
            [
                ReadCode(bits, NumLiteralCodes + NumLengthCodes + cacheSize),
                ReadCode(bits, NumLiteralCodes), ReadCode(bits, NumLiteralCodes), ReadCode(bits, NumLiteralCodes),
                ReadCode(bits, NumDistanceCodes)
            ];
        }

        var total = checked(xsize * height);
        var output = new uint[total];
        var cache = cacheBits > 0 ? new uint[cacheSize] : null;
        var mask = meta != null ? (1 << metaBits) - 1 : -1;
        var subWidth = meta != null ? SubSize(xsize, metaBits) : 0;

        var x = 0;
        var y = 0;
        var position = 0;
        var group = groups[meta != null ? (int)meta[0] : 0];
        while (position < total)
        {
            if ((x & mask) == 0 && meta != null) group = groups[(int)meta[(y >> metaBits) * subWidth + (x >> metaBits)]];

            var green = group[0].Read(bits);
            if (green < NumLiteralCodes)
            {
                var red = group[1].Read(bits);
                var blue = group[2].Read(bits);
                var alpha = group[3].Read(bits);
                var argb = (uint)alpha << 24 | (uint)red << 16 | (uint)green << 8 | (uint)blue;
                output[position++] = argb;
                if (cache != null) cache[(0x1E35A7BDu * argb) >> (32 - cacheBits)] = argb;
                if (++x >= xsize) { x = 0; y++; }
            }
            else if (green < NumLiteralCodes + NumLengthCodes)
            {
                var length = CopyValue(bits, green - NumLiteralCodes);
                var distanceCode = CopyValue(bits, group[4].Read(bits));
                var distance = distanceCode > 120
                    ? distanceCode - 120
                    : Math.Max(1, DistanceMap[distanceCode - 1].X + DistanceMap[distanceCode - 1].Y * xsize);
                if (distance > position || position + length > total) throw new InvalidDataException("The WebP back-reference is out of range.");

                for (var i = 0; i < length; i++)
                {
                    var argb = output[position - distance];
                    output[position++] = argb;
                    if (cache != null) cache[(0x1E35A7BDu * argb) >> (32 - cacheBits)] = argb;
                }
                x += length;
                while (x >= xsize) { x -= xsize; y++; }
                if (position < total && (x & mask) != 0 && meta != null) group = groups[(int)meta[(y >> metaBits) * subWidth + (x >> metaBits)]];
            }
            else
            {
                if (cache == null) throw new InvalidDataException("The WebP uses a colour cache it did not declare.");
                var key = green - NumLiteralCodes - NumLengthCodes;
                if (key >= cacheSize) throw new InvalidDataException("The WebP colour cache index is out of range.");
                var argb = cache[key];
                output[position++] = argb;
                cache[(0x1E35A7BDu * argb) >> (32 - cacheBits)] = argb;
                if (++x >= xsize) { x = 0; y++; }
            }
        }
        return output;
    }

    private static int SubSize(int size, int bits) => (size + (1 << bits) - 1) >> bits;

    /// <summary>An LZ77 length or distance: symbols 0–3 are the values 1–4, then each pair of symbols adds one more extra bit.</summary>
    private static int CopyValue(BitReader bits, int symbol)
    {
        if (symbol < 4) return symbol + 1;
        var extra = (symbol - 2) >> 1;
        var offset = (2 + (symbol & 1)) << extra;
        return offset + (int)bits.Read(extra) + 1;
    }

    // ---- prefix codes ---------------------------------------------------------------------------------------------------------------

    private static HuffmanCode ReadCode(BitReader bits, int alphabetSize)
    {
        var lengths = new int[alphabetSize];
        if (bits.Read(1) != 0) // simple code: one or two symbols given directly
        {
            var count = (int)bits.Read(1) + 1;
            var first = bits.Read(1) != 0 ? (int)bits.Read(8) : (int)bits.Read(1);
            lengths[first] = 1;
            if (count == 2)
            {
                var second = (int)bits.Read(8);
                if (second >= alphabetSize) throw new InvalidDataException("A WebP prefix code symbol is out of range.");
                lengths[second] = 1;
            }
            if (first >= alphabetSize) throw new InvalidDataException("A WebP prefix code symbol is out of range.");
            return HuffmanCode.Build(lengths);
        }

        var codeLengthLengths = new int[19];
        var numCodes = (int)bits.Read(4) + 4;
        for (var i = 0; i < numCodes; i++) codeLengthLengths[CodeLengthOrder[i]] = (int)bits.Read(3);
        var codeLengthCode = HuffmanCode.Build(codeLengthLengths);

        var maxSymbol = alphabetSize;
        if (bits.Read(1) != 0)
        {
            var lengthBits = 2 + 2 * (int)bits.Read(3);
            maxSymbol = 2 + (int)bits.Read(lengthBits);
            if (maxSymbol > alphabetSize) throw new InvalidDataException("The WebP prefix code is too long.");
        }

        var symbol = 0;
        var previous = 8;
        while (symbol < alphabetSize && maxSymbol-- > 0)
        {
            var codeLength = codeLengthCode.Read(bits);
            if (codeLength < 16)
            {
                lengths[symbol++] = codeLength;
                if (codeLength != 0) previous = codeLength;
                continue;
            }

            var usePrevious = codeLength == 16;
            var repeat = codeLength switch
            {
                16 => 3 + (int)bits.Read(2),
                17 => 3 + (int)bits.Read(3),
                _ => 11 + (int)bits.Read(7)
            };
            if (symbol + repeat > alphabetSize) throw new InvalidDataException("The WebP prefix code lengths overrun.");
            var value = usePrevious ? previous : 0;
            for (var i = 0; i < repeat; i++) lengths[symbol++] = value;
        }
        return HuffmanCode.Build(lengths);
    }

    /// <summary>A canonical prefix code, read most-significant code bit first, decoded through a table indexed by the next bits of the stream.</summary>
    private sealed class HuffmanCode
    {
        private readonly int _single;       // >= 0: the code has one symbol and costs no bits
        private readonly ushort[] _table = [];
        private readonly int _tableBits;

        private HuffmanCode(int single) => _single = single;

        private HuffmanCode(ushort[] table, int tableBits)
        {
            _single = -1;
            _table = table;
            _tableBits = tableBits;
        }

        public static HuffmanCode Build(int[] lengths)
        {
            var used = 0;
            var only = 0;
            var maxLength = 0;
            for (var s = 0; s < lengths.Length; s++)
            {
                if (lengths[s] == 0) continue;
                used++; only = s;
                if (lengths[s] > maxLength) maxLength = lengths[s];
            }
            if (used == 0) throw new InvalidDataException("A WebP prefix code has no symbols.");
            if (used == 1) return new HuffmanCode(only);
            if (maxLength > MaxCodeLength) throw new InvalidDataException("A WebP prefix code is too long.");

            var counts = new int[MaxCodeLength + 2];
            foreach (var l in lengths) counts[l]++;
            counts[0] = 0;

            // the code must be complete: the lengths use up the whole code space exactly
            var space = 1;
            for (var l = 1; l <= maxLength; l++)
            {
                space = (space << 1) - counts[l];
                if (space < 0) throw new InvalidDataException("A WebP prefix code is over-subscribed.");
            }
            if (space != 0) throw new InvalidDataException("A WebP prefix code is incomplete.");

            var next = new int[MaxCodeLength + 2];
            var code = 0;
            for (var l = 1; l <= maxLength; l++)
            {
                code = (code + counts[l - 1]) << 1;
                next[l] = code;
            }

            var table = new ushort[1 << maxLength];
            for (var s = 0; s < lengths.Length; s++)
            {
                var l = lengths[s];
                if (l == 0) continue;
                var c = next[l]++;
                var reversed = 0; // the stream delivers the code's first bit first, which is its most significant
                for (var i = 0; i < l; i++) reversed |= ((c >> i) & 1) << (l - 1 - i);
                for (var fill = reversed; fill < table.Length; fill += 1 << l) table[fill] = (ushort)(s | l << 12);
            }
            return new HuffmanCode(table, maxLength);
        }

        public int Read(BitReader bits)
        {
            if (_single >= 0) return _single;
            var entry = _table[bits.Peek(_tableBits)];
            bits.Skip(entry >> 12);
            return entry & 0xFFF;
        }
    }

    // ---- bits -----------------------------------------------------------------------------------------------------------------------

    private sealed class BitReader(ReadOnlySpan<byte> data)
    {
        private readonly byte[] _data = data.ToArray();
        private int _position;
        private ulong _buffer;
        private int _count;

        private void Fill()
        {
            while (_count <= 56 && _position < _data.Length)
            {
                _buffer |= (ulong)_data[_position++] << _count;
                _count += 8;
            }
        }

        public uint Read(int n)
        {
            if (n == 0) return 0;
            Fill();
            if (_count < n) throw new InvalidDataException("The WebP data is truncated.");
            var value = (uint)(_buffer & ((1UL << n) - 1));
            _buffer >>= n;
            _count -= n;
            return value;
        }

        /// <summary>The next n bits without consuming them; missing bits at the very end read as zero.</summary>
        public int Peek(int n)
        {
            Fill();
            return (int)(_buffer & ((1UL << n) - 1));
        }

        public void Skip(int n)
        {
            if (_count < n) throw new InvalidDataException("The WebP data is truncated.");
            _buffer >>= n;
            _count -= n;
        }
    }

    // ---- transforms -----------------------------------------------------------------------------------------------------------------

    private static uint AddPixels(uint a, uint b)
    {
        var alphaGreen = (a & 0xFF00FF00u) + (b & 0xFF00FF00u);
        var redBlue = (a & 0x00FF00FFu) + (b & 0x00FF00FFu);
        return (alphaGreen & 0xFF00FF00u) | (redBlue & 0x00FF00FFu);
    }

    private static uint Average2(uint a, uint b) => (((a ^ b) & 0xFEFEFEFEu) >> 1) + (a & b);

    private static int Clamp255(int v) => v < 0 ? 0 : v > 255 ? 255 : v;

    private static uint Select(uint left, uint top, uint topLeft)
    {
        var pLeft = 0;
        var pTop = 0;
        for (var shift = 0; shift < 32; shift += 8)
        {
            int l = (int)(left >> shift) & 0xFF, t = (int)(top >> shift) & 0xFF, tl = (int)(topLeft >> shift) & 0xFF;
            var estimate = l + t - tl;
            pLeft += Math.Abs(estimate - l);
            pTop += Math.Abs(estimate - t);
        }
        return pLeft < pTop ? left : top;
    }

    private static uint ClampAddSubtractFull(uint a, uint b, uint c)
    {
        uint result = 0;
        for (var shift = 0; shift < 32; shift += 8)
            result |= (uint)Clamp255((int)(a >> shift & 0xFF) + (int)(b >> shift & 0xFF) - (int)(c >> shift & 0xFF)) << shift;
        return result;
    }

    private static uint ClampAddSubtractHalf(uint a, uint b)
    {
        uint result = 0;
        for (var shift = 0; shift < 32; shift += 8)
        {
            int av = (int)(a >> shift) & 0xFF, bv = (int)(b >> shift) & 0xFF;
            result |= (uint)Clamp255(av + (av - bv) / 2) << shift;
        }
        return result;
    }

    private static uint[] InversePredictor(uint[] pixels, int width, int height, Transform transform)
    {
        var blocksAcross = SubSize(width, transform.Bits);
        for (var y = 0; y < height; y++)
        {
            var row = y * width;
            for (var x = 0; x < width; x++)
            {
                var i = row + x;
                uint prediction;
                if (y == 0) prediction = x == 0 ? 0xFF000000u : pixels[i - 1];
                else if (x == 0) prediction = pixels[i - width];
                else
                {
                    var mode = (int)(transform.Data[(y >> transform.Bits) * blocksAcross + (x >> transform.Bits)] >> 8) & 0xF;
                    uint left = pixels[i - 1], top = pixels[i - width], topLeft = pixels[i - width - 1];
                    var topRight = pixels[i - width + 1]; // on the last column this is the first pixel of the current row
                    prediction = mode switch
                    {
                        1 => left,
                        2 => top,
                        3 => topRight,
                        4 => topLeft,
                        5 => Average2(Average2(left, topRight), top),
                        6 => Average2(left, topLeft),
                        7 => Average2(left, top),
                        8 => Average2(topLeft, top),
                        9 => Average2(top, topRight),
                        10 => Average2(Average2(left, topLeft), Average2(top, topRight)),
                        11 => Select(left, top, topLeft),
                        12 => ClampAddSubtractFull(left, top, topLeft),
                        13 => ClampAddSubtractHalf(Average2(left, top), topLeft),
                        _ => 0xFF000000u
                    };
                }
                pixels[i] = AddPixels(pixels[i], prediction);
            }
        }
        return pixels;
    }

    private static uint[] InverseCrossColour(uint[] pixels, int width, int height, Transform transform)
    {
        var blocksAcross = SubSize(width, transform.Bits);
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var element = transform.Data[(y >> transform.Bits) * blocksAcross + (x >> transform.Bits)];
                int greenToRed = (sbyte)element, greenToBlue = (sbyte)(element >> 8), redToBlue = (sbyte)(element >> 16);

                var i = y * width + x;
                var argb = pixels[i];
                int green = (sbyte)(argb >> 8);
                var red = (int)(argb >> 16) & 0xFF;
                var blue = (int)argb & 0xFF;
                red = (red + ((greenToRed * green) >> 5)) & 0xFF;
                blue = (blue + ((greenToBlue * green) >> 5) + ((redToBlue * (sbyte)red) >> 5)) & 0xFF;
                pixels[i] = (argb & 0xFF00FF00u) | (uint)red << 16 | (uint)blue;
            }
        return pixels;
    }

    private static uint[] InverseSubtractGreen(uint[] pixels)
    {
        for (var i = 0; i < pixels.Length; i++)
        {
            var argb = pixels[i];
            var green = (argb >> 8) & 0xFF;
            var redBlue = ((argb & 0x00FF00FFu) + (green << 16 | green)) & 0x00FF00FFu;
            pixels[i] = (argb & 0xFF00FF00u) | redBlue;
        }
        return pixels;
    }

    private static uint[] InverseColourIndexing(uint[] pixels, int outputWidth, int height, Transform transform)
    {
        var table = transform.Data;
        var result = new uint[checked(outputWidth * height)];
        var packedWidth = SubSize(outputWidth, transform.Bits);
        var bitsPerPixel = 8 >> transform.Bits;
        var pixelMask = (1 << bitsPerPixel) - 1;

        for (var y = 0; y < height; y++)
            for (var x = 0; x < outputWidth; x++)
            {
                var packed = (pixels[y * packedWidth + (x >> transform.Bits)] >> 8) & 0xFF;
                var index = (int)(packed >> ((x & ((1 << transform.Bits) - 1)) * bitsPerPixel)) & pixelMask;
                if (transform.Bits == 0) index = (int)packed;
                result[y * outputWidth + x] = index < table.Length ? table[index] : 0u; // an index past the palette is transparent black
            }
        return result;
    }
}
