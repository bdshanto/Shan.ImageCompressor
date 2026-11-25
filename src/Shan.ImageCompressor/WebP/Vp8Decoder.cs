namespace Shan.ImageCompressor.WebP;

/// <summary>
/// Decodes a lossy WebP image: a single VP8 key frame (RFC 6386). Intra prediction, the integer inverse transforms, the in-loop
/// deblocking filter and the "fancy" chroma upsampling follow the reference decoder, so the pixels match libwebp's exactly.
/// </summary>
internal static class Vp8Decoder
{
    public static RgbaImage Decode(ReadOnlySpan<byte> data, long maxPixels) => new Frame(data.ToArray(), maxPixels).Decode();

    private const int Bps = 32; // stride of the per-macroblock work buffers

    // libwebp's mode numbering: the 16×16 and chroma modes reuse the first four 4×4 numbers.
    private const int DcPred = 0, TmPred = 1, VPred = 2, HPred = 3;
    private const int BRdPred = 4, BVrPred = 5, BLdPred = 6, BVlPred = 7, BHdPred = 8, BHuPred = 9;

    private static readonly sbyte[] SubBlockTree =
    [
        -0, 1, -1, 2, -2, 3, 4, 6, -3, 5, -4, -5, -6, 7, -7, 8, -8, -9
    ];

    private static readonly byte[] Cat3 = [173, 148, 140, 0];
    private static readonly byte[] Cat4 = [176, 155, 140, 135, 0];
    private static readonly byte[] Cat5 = [180, 157, 141, 134, 130, 0];
    private static readonly byte[] Cat6 = [254, 254, 243, 230, 196, 177, 153, 140, 133, 130, 129, 0];
    private static readonly byte[][] Cat3456 = [Cat3, Cat4, Cat5, Cat6];

    // ---- boolean entropy decoder -----------------------------------------------------------------------------------------------------

    private sealed class BoolDecoder
    {
        private readonly byte[] _data;
        private readonly int _end;
        private int _position;
        private uint _value;
        private int _range = 255;
        private int _bitCount;

        public BoolDecoder(byte[] data, int start, int end)
        {
            _data = data;
            _end = end;
            _position = start;
            _value = (uint)(Next() << 8 | Next());
        }

        /// <summary>How many bytes were wanted beyond the end of the partition (a well-formed stream needs at most the two read ahead).</summary>
        public int Overrun { get; private set; }

        private int Next()
        {
            if (_position < _end) return _data[_position++];
            Overrun++;
            return 0;
        }

        public int GetBit(int probability)
        {
            var split = 1 + (((_range - 1) * probability) >> 8);
            var bigSplit = (uint)split << 8;
            int bit;
            if (_value >= bigSplit)
            {
                bit = 1;
                _range -= split;
                _value -= bigSplit;
            }
            else
            {
                bit = 0;
                _range = split;
            }
            while (_range < 128)
            {
                _value <<= 1;
                _range <<= 1;
                if (++_bitCount == 8)
                {
                    _bitCount = 0;
                    _value |= (uint)Next();
                }
            }
            return bit;
        }

        public int Get() => GetBit(128);

        public int GetValue(int bits)
        {
            var v = 0;
            while (bits-- > 0) v |= GetBit(128) << bits;
            return v;
        }

        public int GetSignedValue(int bits)
        {
            var v = GetValue(bits);
            return Get() != 0 ? -v : v;
        }

        public int GetSigned(int v) => GetBit(128) != 0 ? -v : v;
    }

    // ---- the frame --------------------------------------------------------------------------------------------------------------------

    private sealed class Frame
    {
        private readonly byte[] _data;
        private readonly long _maxPixels;

        private int _width, _height, _mbWidth, _mbHeight;
        private BoolDecoder _header = null!;

        // segment header
        private bool _segmentsEnabled, _updateSegmentMap, _absoluteSegmentValues;
        private readonly int[] _segmentQuantizer = new int[4];
        private readonly int[] _segmentFilter = new int[4];
        private readonly int[] _segmentProbs = [255, 255, 255];

        // loop filter header
        private bool _simpleFilter, _useFilterDeltas;
        private int _filterLevel, _sharpness;
        private readonly int[] _refDelta = new int[4];
        private readonly int[] _modeDelta = new int[4];

        // quantisation
        private int _baseQ, _dqY1Dc, _dqY2Dc, _dqY2Ac, _dqUvDc, _dqUvAc;
        private readonly int[][] _y1 = new int[4][], _y2 = new int[4][], _uv = new int[4][];

        private readonly byte[] _probabilities = new byte[4 * 8 * 3 * 11];
        private bool _useSkipProbability;
        private int _skipProbability;

        private int _firstPartitionSize;
        private int _partitionCount;
        private BoolDecoder[] _partitions = [];

        private byte[] _yPlane = [], _uPlane = [], _vPlane = [];
        private int _yStride, _uvStride;

        // per-macroblock facts the loop filter needs afterwards
        private byte[] _mbSegment = [];
        private bool[] _mbIntra4 = [];
        private bool[] _mbInner = [];

        public Frame(byte[] data, long maxPixels)
        {
            _data = data;
            _maxPixels = maxPixels;
        }

        public RgbaImage Decode()
        {
            ReadFrameHeader();
            ReadHeader();
            SetUpPartitions();
            PrepareQuantisers();
            DecodeMacroblocks();
            if (_header.Overrun > 3) throw new InvalidDataException("The WebP data is truncated.");
            if (FilterType() != 0) Filter();
            return ToRgba();
        }

        private void ReadFrameHeader()
        {
            if (_data.Length < 10) throw new InvalidDataException("The WebP data is truncated.");
            var tag = _data[0] | _data[1] << 8 | _data[2] << 16;
            if ((tag & 1) != 0) throw new InvalidDataException("The WebP lossy frame is not a key frame.");
            if (((tag >> 1) & 7) > 3) throw new NotSupportedException("This WebP lossy profile is not supported.");
            if (((tag >> 4) & 1) == 0) throw new InvalidDataException("The WebP lossy frame is not displayable.");
            _firstPartitionSize = tag >> 5;
            if (_data[3] != 0x9D || _data[4] != 0x01 || _data[5] != 0x2A) throw new InvalidDataException("The WebP lossy frame has a bad start code.");

            _width = (_data[6] | _data[7] << 8) & 0x3FFF;
            _height = (_data[8] | _data[9] << 8) & 0x3FFF;
            if (_width == 0 || _height == 0) throw new InvalidDataException("The WebP has invalid dimensions.");
            if ((long)_width * _height > _maxPixels) throw new InvalidDataException($"The image has {(long)_width * _height} pixels, over the {_maxPixels} limit.");
            if (10L + _firstPartitionSize > _data.Length) throw new InvalidDataException("The WebP data is truncated.");

            _mbWidth = (_width + 15) >> 4;
            _mbHeight = (_height + 15) >> 4;
        }

        private void ReadHeader()
        {
            var br = _header = new BoolDecoder(_data, 10, 10 + _firstPartitionSize);
            br.Get(); // colour space
            br.Get(); // clamping type (the decoder always clamps)

            _segmentsEnabled = br.Get() != 0;
            if (_segmentsEnabled)
            {
                _updateSegmentMap = br.Get() != 0;
                if (br.Get() != 0) // update segment feature data
                {
                    _absoluteSegmentValues = br.Get() != 0;
                    for (var s = 0; s < 4; s++) _segmentQuantizer[s] = br.Get() != 0 ? br.GetSignedValue(7) : 0;
                    for (var s = 0; s < 4; s++) _segmentFilter[s] = br.Get() != 0 ? br.GetSignedValue(6) : 0;
                }
                if (_updateSegmentMap)
                    for (var s = 0; s < 3; s++) _segmentProbs[s] = br.Get() != 0 ? br.GetValue(8) : 255;
            }

            _simpleFilter = br.Get() != 0;
            _filterLevel = br.GetValue(6);
            _sharpness = br.GetValue(3);
            _useFilterDeltas = br.Get() != 0;
            if (_useFilterDeltas && br.Get() != 0) // the deltas are being updated
            {
                for (var i = 0; i < 4; i++) if (br.Get() != 0) _refDelta[i] = br.GetSignedValue(6);
                for (var i = 0; i < 4; i++) if (br.Get() != 0) _modeDelta[i] = br.GetSignedValue(6);
            }

            _partitionCount = 1 << br.GetValue(2);

            _baseQ = br.GetValue(7);
            _dqY1Dc = br.Get() != 0 ? br.GetSignedValue(4) : 0;
            _dqY2Dc = br.Get() != 0 ? br.GetSignedValue(4) : 0;
            _dqY2Ac = br.Get() != 0 ? br.GetSignedValue(4) : 0;
            _dqUvDc = br.Get() != 0 ? br.GetSignedValue(4) : 0;
            _dqUvAc = br.Get() != 0 ? br.GetSignedValue(4) : 0;

            br.Get(); // refresh_entropy_probs: meaningless for a single frame

            for (var i = 0; i < _probabilities.Length; i++)
                _probabilities[i] = br.GetBit(Vp8Tables.CoefficientUpdate[i]) != 0 ? (byte)br.GetValue(8) : Vp8Tables.CoefficientDefaults[i];

            _useSkipProbability = br.Get() != 0;
            if (_useSkipProbability) _skipProbability = br.GetValue(8);
        }

        private void SetUpPartitions()
        {
            var sizesAt = 10 + _firstPartitionSize;
            var start = sizesAt + 3 * (_partitionCount - 1);
            if (start > _data.Length) throw new InvalidDataException("The WebP partition table is truncated.");

            _partitions = new BoolDecoder[_partitionCount];
            for (var p = 0; p < _partitionCount; p++)
            {
                int size;
                if (p < _partitionCount - 1)
                {
                    size = _data[sizesAt + 3 * p] | _data[sizesAt + 3 * p + 1] << 8 | _data[sizesAt + 3 * p + 2] << 16;
                    if (start + (long)size > _data.Length) size = _data.Length - start; // a short partition is read as far as it goes
                }
                else size = _data.Length - start;
                _partitions[p] = new BoolDecoder(_data, start, start + size);
                start += size;
            }
        }

        private void PrepareQuantisers()
        {
            static int Clip(int v, int max) => v < 0 ? 0 : v > max ? max : v;

            for (var s = 0; s < 4; s++)
            {
                int q;
                if (_segmentsEnabled) q = _absoluteSegmentValues ? _segmentQuantizer[s] : _baseQ + _segmentQuantizer[s];
                else q = _baseQ;

                _y1[s] = [Vp8Tables.DcQuant[Clip(q + _dqY1Dc, 127)], Vp8Tables.AcQuant[Clip(q, 127)]];
                _y2[s] = [Vp8Tables.DcQuant[Clip(q + _dqY2Dc, 127)] * 2, Math.Max(8, Vp8Tables.AcQuant[Clip(q + _dqY2Ac, 127)] * 155 / 100)];
                _uv[s] = [Vp8Tables.DcQuant[Clip(q + _dqUvDc, 117)], Vp8Tables.AcQuant[Clip(q + _dqUvAc, 127)]];
            }
        }

        // ---- macroblocks -------------------------------------------------------------------------------------------------------------

        private static int ProbabilityIndex(int type, int band, int context) => ((type * 8 + band) * 3 + context) * 11;

        private void DecodeMacroblocks()
        {
            _yStride = _mbWidth * 16;
            _uvStride = _mbWidth * 8;
            _yPlane = new byte[checked(_yStride * _mbHeight * 16)];
            _uPlane = new byte[checked(_uvStride * _mbHeight * 8)];
            _vPlane = new byte[_uPlane.Length];
            _mbSegment = new byte[_mbWidth * _mbHeight];
            _mbIntra4 = new bool[_mbWidth * _mbHeight];
            _mbInner = new bool[_mbWidth * _mbHeight];

            var topModes = new byte[_mbWidth * 4];                  // 4×4 prediction mode of the bottom row of each macroblock above
            var leftModes = new byte[4];
            var topNzY = new byte[_mbWidth * 4];
            var topNzU = new byte[_mbWidth * 2];
            var topNzV = new byte[_mbWidth * 2];
            var topNzDc = new byte[_mbWidth];
            var leftNzY = new byte[4];
            var leftNzU = new byte[2];
            var leftNzV = new byte[2];
            var leftNzDc = 0;

            var coefficients = new short[384];
            var modes = new byte[16];
            var workY = new byte[Bps * 17 + 8 + 24];
            var workU = new byte[Bps * 9 + 8 + 8];
            var workV = new byte[Bps * 9 + 8 + 8];

            for (var mbY = 0; mbY < _mbHeight; mbY++)
            {
                var tokens = _partitions[mbY & (_partitionCount - 1)];
                Array.Clear(leftModes, 0, leftModes.Length);
                Array.Clear(leftNzY, 0, leftNzY.Length); Array.Clear(leftNzU, 0, leftNzU.Length); Array.Clear(leftNzV, 0, leftNzV.Length);
                leftNzDc = 0;

                for (var mbX = 0; mbX < _mbWidth; mbX++)
                {
                    var index = mbY * _mbWidth + mbX;

                    // ---- header: segment, skip flag, prediction modes (first partition)
                    var segment = 0;
                    if (_updateSegmentMap)
                        segment = _header.GetBit(_segmentProbs[0]) == 0 ? _header.GetBit(_segmentProbs[1]) : _header.GetBit(_segmentProbs[2]) + 2;
                    var skip = _useSkipProbability && _header.GetBit(_skipProbability) != 0;

                    var intra4 = _header.GetBit(145) == 0;
                    var lumaMode = 0;
                    if (!intra4)
                    {
                        lumaMode = _header.GetBit(156) != 0
                            ? (_header.GetBit(128) != 0 ? TmPred : HPred)
                            : (_header.GetBit(163) != 0 ? VPred : DcPred);
                        for (var i = 0; i < 4; i++) topModes[mbX * 4 + i] = (byte)lumaMode;
                        Array.Fill(leftModes, (byte)lumaMode);
                    }
                    else
                    {
                        for (var y = 0; y < 4; y++)
                        {
                            int left = leftModes[y];
                            for (var x = 0; x < 4; x++)
                            {
                                var p = (topModes[mbX * 4 + x] * 10 + left) * 9;
                                int node = SubBlockTree[_header.GetBit(Vp8Tables.SubBlockModes[p])];
                                while (node > 0) node = SubBlockTree[2 * node + _header.GetBit(Vp8Tables.SubBlockModes[p + node])];
                                left = -node;
                                topModes[mbX * 4 + x] = (byte)left;
                                modes[y * 4 + x] = (byte)left;
                            }
                            leftModes[y] = (byte)left;
                        }
                    }
                    var chromaMode = _header.GetBit(142) == 0 ? DcPred : _header.GetBit(114) == 0 ? VPred : _header.GetBit(183) != 0 ? TmPred : HPred;

                    // ---- residual coefficients (token partition)
                    Array.Clear(coefficients, 0, coefficients.Length);
                    var hasCoefficients = false;
                    if (!skip)
                    {
                        hasCoefficients = ParseResiduals(tokens, segment, intra4, mbX, coefficients,
                            topNzY, topNzU, topNzV, topNzDc, leftNzY, leftNzU, leftNzV, ref leftNzDc);
                    }
                    else
                    {
                        for (var i = 0; i < 4; i++) { topNzY[mbX * 4 + i] = 0; leftNzY[i] = 0; }
                        for (var i = 0; i < 2; i++) { topNzU[mbX * 2 + i] = 0; leftNzU[i] = 0; topNzV[mbX * 2 + i] = 0; leftNzV[i] = 0; }
                        if (!intra4) { topNzDc[mbX] = 0; leftNzDc = 0; }
                    }
                    if (tokens.Overrun > 3) throw new InvalidDataException("The WebP data is truncated.");

                    _mbSegment[index] = (byte)segment;
                    _mbIntra4[index] = intra4;
                    _mbInner[index] = intra4 || hasCoefficients;

                    Reconstruct(mbX, mbY, intra4, lumaMode, modes, chromaMode, coefficients, workY, workU, workV);
                }
            }
        }

        /// <summary>Reads one macroblock's coefficients, updating the neighbour contexts; returns whether any coefficient is non-zero.</summary>
        private bool ParseResiduals(BoolDecoder br, int segment, bool intra4, int mbX, short[] output,
            byte[] topNzY, byte[] topNzU, byte[] topNzV, byte[] topNzDc, byte[] leftNzY, byte[] leftNzU, byte[] leftNzV, ref int leftNzDc)
        {
            var y1 = _y1[segment];
            var y2 = _y2[segment];
            var uv = _uv[segment];
            int firstCoefficient, lumaType;

            if (!intra4)
            {
                // the 16 luma DC values are coded together (type 1), then transformed by the Walsh-Hadamard transform
                var dc = new short[16];
                var context = topNzDc[mbX] + leftNzDc;
                var n = GetCoefficients(br, 1, context, y2, 0, dc, 0);
                topNzDc[mbX] = (byte)(n > 0 ? 1 : 0);
                leftNzDc = n > 0 ? 1 : 0;
                InverseWalshHadamard(dc, output);
                firstCoefficient = 1;
                lumaType = 0;
            }
            else
            {
                firstCoefficient = 0;
                lumaType = 3;
            }

            var any = false;
            for (var y = 0; y < 4; y++)
            {
                for (var x = 0; x < 4; x++)
                {
                    var block = y * 4 + x;
                    var context = topNzY[mbX * 4 + x] + leftNzY[y];
                    var n = GetCoefficients(br, lumaType, context, y1, firstCoefficient, output, block * 16);
                    var flag = (byte)(n > firstCoefficient ? 1 : 0);
                    topNzY[mbX * 4 + x] = flag;
                    leftNzY[y] = flag;
                    if (n > 1 || output[block * 16] != 0) any = true;
                }
            }

            for (var plane = 0; plane < 2; plane++)
            {
                var top = plane == 0 ? topNzU : topNzV;
                var left = plane == 0 ? leftNzU : leftNzV;
                for (var y = 0; y < 2; y++)
                    for (var x = 0; x < 2; x++)
                    {
                        var block = 16 + plane * 4 + y * 2 + x;
                        var context = top[mbX * 2 + x] + left[y];
                        var n = GetCoefficients(br, 2, context, uv, 0, output, block * 16);
                        var flag = (byte)(n > 0 ? 1 : 0);
                        top[mbX * 2 + x] = flag;
                        left[y] = flag;
                        if (n > 1 || output[block * 16] != 0) any = true;
                    }
            }
            return any;
        }

        /// <summary>Reads the tokens of one 4×4 block; returns the position after the last non-zero coefficient (or <paramref name="n"/> if there is none).</summary>
        private int GetCoefficients(BoolDecoder br, int type, int context, int[] quant, int n, short[] output, int offset)
        {
            var probabilities = _probabilities;
            var p = ProbabilityIndex(type, Vp8Tables.Bands[n], context);
            for (; n < 16; n++)
            {
                if (br.GetBit(probabilities[p]) == 0) return n; // end of block
                while (br.GetBit(probabilities[p + 1]) == 0) // a run of zeros
                {
                    p = ProbabilityIndex(type, Vp8Tables.Bands[++n], 0);
                    if (n == 16) return 16;
                }

                int value;
                var next = Vp8Tables.Bands[n + 1];
                if (br.GetBit(probabilities[p + 2]) == 0)
                {
                    value = 1;
                    p = ProbabilityIndex(type, next, 1);
                }
                else
                {
                    value = GetLargeValue(br, p);
                    p = ProbabilityIndex(type, next, 2);
                }
                output[offset + Vp8Tables.Zigzag[n]] = (short)(br.GetSigned(value) * quant[n > 0 ? 1 : 0]);
            }
            return 16;
        }

        private int GetLargeValue(BoolDecoder br, int p)
        {
            var probabilities = _probabilities;
            int v;
            if (br.GetBit(probabilities[p + 3]) == 0)
            {
                v = br.GetBit(probabilities[p + 4]) == 0 ? 2 : 3 + br.GetBit(probabilities[p + 5]);
            }
            else if (br.GetBit(probabilities[p + 6]) == 0)
            {
                if (br.GetBit(probabilities[p + 7]) == 0) v = 5 + br.GetBit(159);
                else
                {
                    v = 7 + 2 * br.GetBit(165);
                    v += br.GetBit(145);
                }
            }
            else
            {
                var bit1 = br.GetBit(probabilities[p + 8]);
                var bit0 = br.GetBit(probabilities[p + 9 + bit1]);
                var category = 2 * bit1 + bit0;
                v = 0;
                foreach (var probability in Cat3456[category])
                {
                    if (probability == 0) break;
                    v += v + br.GetBit(probability);
                }
                v += 3 + (8 << category);
            }
            return v;
        }

        private static void InverseWalshHadamard(short[] input, short[] output)
        {
            Span<int> tmp = stackalloc int[16];
            for (var i = 0; i < 4; i++)
            {
                var a0 = input[0 + i] + input[12 + i];
                var a1 = input[4 + i] + input[8 + i];
                var a2 = input[4 + i] - input[8 + i];
                var a3 = input[0 + i] - input[12 + i];
                tmp[0 + i] = a0 + a1;
                tmp[8 + i] = a0 - a1;
                tmp[4 + i] = a3 + a2;
                tmp[12 + i] = a3 - a2;
            }
            for (var i = 0; i < 4; i++)
            {
                var dc = tmp[0 + i * 4] + 3;
                var a0 = dc + tmp[3 + i * 4];
                var a1 = tmp[1 + i * 4] + tmp[2 + i * 4];
                var a2 = tmp[1 + i * 4] - tmp[2 + i * 4];
                var a3 = dc - tmp[3 + i * 4];
                output[(i * 4 + 0) * 16] = (short)((a0 + a1) >> 3);
                output[(i * 4 + 1) * 16] = (short)((a3 + a2) >> 3);
                output[(i * 4 + 2) * 16] = (short)((a0 - a1) >> 3);
                output[(i * 4 + 3) * 16] = (short)((a3 - a2) >> 3);
            }
        }

        // ---- reconstruction ---------------------------------------------------------------------------------------------------------

        private const int YOffset = Bps + 8; // where the 16×16 block starts inside its work buffer: one row of "above" pixels, 8 columns of room

        private void Reconstruct(int mbX, int mbY, bool intra4, int lumaMode, byte[] modes, int chromaMode, short[] coefficients,
            byte[] workY, byte[] workU, byte[] workV)
        {
            // ---- neighbours: the row above, the column to the left, the corner; outside the picture they are fixed 127 / 129
            for (var j = 0; j < 16; j++)
                workY[YOffset + j * Bps - 1] = mbX > 0 ? _yPlane[(mbY * 16 + j) * _yStride + mbX * 16 - 1] : (byte)129;
            for (var j = 0; j < 8; j++)
            {
                workU[YOffset + j * Bps - 1] = mbX > 0 ? _uPlane[(mbY * 8 + j) * _uvStride + mbX * 8 - 1] : (byte)129;
                workV[YOffset + j * Bps - 1] = mbX > 0 ? _vPlane[(mbY * 8 + j) * _uvStride + mbX * 8 - 1] : (byte)129;
            }

            if (mbY > 0)
            {
                Buffer.BlockCopy(_yPlane, ((mbY * 16) - 1) * _yStride + mbX * 16, workY, YOffset - Bps, 16);
                Buffer.BlockCopy(_uPlane, ((mbY * 8) - 1) * _uvStride + mbX * 8, workU, YOffset - Bps, 8);
                Buffer.BlockCopy(_vPlane, ((mbY * 8) - 1) * _uvStride + mbX * 8, workV, YOffset - Bps, 8);
                var corner = mbX > 0;
                workY[YOffset - Bps - 1] = corner ? _yPlane[(mbY * 16 - 1) * _yStride + mbX * 16 - 1] : (byte)129;
                workU[YOffset - Bps - 1] = corner ? _uPlane[(mbY * 8 - 1) * _uvStride + mbX * 8 - 1] : (byte)129;
                workV[YOffset - Bps - 1] = corner ? _vPlane[(mbY * 8 - 1) * _uvStride + mbX * 8 - 1] : (byte)129;
            }
            else
            {
                Array.Fill(workY, (byte)127, YOffset - Bps - 1, 16 + 4 + 1);
                Array.Fill(workU, (byte)127, YOffset - Bps - 1, 8 + 1);
                Array.Fill(workV, (byte)127, YOffset - Bps - 1, 8 + 1);
            }

            if (!intra4)
            {
                PredictBlock(workY, YOffset, 16, lumaMode, mbX, mbY, 5);
                for (var block = 0; block < 16; block++)
                    AddResidual(coefficients, block * 16, workY, YOffset + (block >> 2) * 4 * Bps + (block & 3) * 4);
            }
            else
            {
                // the four pixels above and to the right of the macroblock, repeated down the right edge for the blocks that need them
                var topRight = YOffset - Bps + 16;
                if (mbY > 0)
                {
                    if (mbX >= _mbWidth - 1) Array.Fill(workY, _yPlane[(mbY * 16 - 1) * _yStride + mbX * 16 + 15], topRight, 4);
                    else Buffer.BlockCopy(_yPlane, (mbY * 16 - 1) * _yStride + (mbX + 1) * 16, workY, topRight, 4);
                }
                for (var row = 1; row <= 3; row++) Buffer.BlockCopy(workY, topRight, workY, topRight + row * 4 * Bps, 4);

                for (var block = 0; block < 16; block++)
                {
                    var dst = YOffset + (block >> 2) * 4 * Bps + (block & 3) * 4;
                    PredictSubBlock(workY, dst, modes[block]);
                    AddResidual(coefficients, block * 16, workY, dst);
                }
            }

            PredictBlock(workU, YOffset, 8, chromaMode, mbX, mbY, 4);
            PredictBlock(workV, YOffset, 8, chromaMode, mbX, mbY, 4);
            for (var block = 0; block < 4; block++)
            {
                var dst = YOffset + (block >> 1) * 4 * Bps + (block & 1) * 4;
                AddResidual(coefficients, (16 + block) * 16, workU, dst);
                AddResidual(coefficients, (20 + block) * 16, workV, dst);
            }

            for (var j = 0; j < 16; j++) Buffer.BlockCopy(workY, YOffset + j * Bps, _yPlane, (mbY * 16 + j) * _yStride + mbX * 16, 16);
            for (var j = 0; j < 8; j++)
            {
                Buffer.BlockCopy(workU, YOffset + j * Bps, _uPlane, (mbY * 8 + j) * _uvStride + mbX * 8, 8);
                Buffer.BlockCopy(workV, YOffset + j * Bps, _vPlane, (mbY * 8 + j) * _uvStride + mbX * 8, 8);
            }
        }

        private static byte Clip(int v) => v < 0 ? (byte)0 : v > 255 ? (byte)255 : (byte)v;

        private static int Mul1(int a) => ((a * 20091) >> 16) + a;
        private static int Mul2(int a) => (a * 35468) >> 16;

        /// <summary>Inverse 4×4 DCT of one block, added to the prediction already in <paramref name="dst"/>.</summary>
        private static void AddResidual(short[] coefficients, int offset, byte[] dst, int dstOffset)
        {
            var any = false;
            for (var i = 0; i < 16; i++)
                if (coefficients[offset + i] != 0) { any = true; break; }
            if (!any) return;

            Span<int> tmp = stackalloc int[16];
            for (var i = 0; i < 4; i++) // vertical pass over each column
            {
                var a = coefficients[offset + i] + coefficients[offset + 8 + i];
                var b = coefficients[offset + i] - coefficients[offset + 8 + i];
                var c = Mul2(coefficients[offset + 4 + i]) - Mul1(coefficients[offset + 12 + i]);
                var d = Mul1(coefficients[offset + 4 + i]) + Mul2(coefficients[offset + 12 + i]);
                tmp[i * 4 + 0] = a + d;
                tmp[i * 4 + 1] = b + c;
                tmp[i * 4 + 2] = b - c;
                tmp[i * 4 + 3] = a - d;
            }
            for (var i = 0; i < 4; i++) // horizontal pass, one output row each
            {
                var dc = tmp[0 + i] + 4;
                var a = dc + tmp[8 + i];
                var b = dc - tmp[8 + i];
                var c = Mul2(tmp[4 + i]) - Mul1(tmp[12 + i]);
                var d = Mul1(tmp[4 + i]) + Mul2(tmp[12 + i]);
                var row = dstOffset + i * Bps;
                dst[row + 0] = Clip(dst[row + 0] + ((a + d) >> 3));
                dst[row + 1] = Clip(dst[row + 1] + ((b + c) >> 3));
                dst[row + 2] = Clip(dst[row + 2] + ((b - c) >> 3));
                dst[row + 3] = Clip(dst[row + 3] + ((a - d) >> 3));
            }
        }

        // ---- intra prediction --------------------------------------------------------------------------------------------------------

        private static void Fill(byte[] buffer, int dst, int size, byte value)
        {
            for (var y = 0; y < size; y++) Array.Fill(buffer, value, dst + y * Bps, size);
        }

        private static void PredictBlock(byte[] b, int dst, int size, int mode, int mbX, int mbY, int shift)
        {
            switch (mode)
            {
                case DcPred:
                    int dc;
                    if (mbX > 0 && mbY > 0)
                    {
                        dc = size;
                        for (var i = 0; i < size; i++) dc += b[dst + i - Bps] + b[dst - 1 + i * Bps];
                        dc >>= shift;
                    }
                    else if (mbY > 0) // no left neighbour: the row above only
                    {
                        dc = size >> 1;
                        for (var i = 0; i < size; i++) dc += b[dst + i - Bps];
                        dc >>= shift - 1;
                    }
                    else if (mbX > 0) // no row above: the column to the left only
                    {
                        dc = size >> 1;
                        for (var i = 0; i < size; i++) dc += b[dst - 1 + i * Bps];
                        dc >>= shift - 1;
                    }
                    else dc = 0x80;
                    Fill(b, dst, size, (byte)dc);
                    break;
                case TmPred:
                    for (var y = 0; y < size; y++)
                        for (var x = 0; x < size; x++)
                            b[dst + y * Bps + x] = Clip(b[dst + x - Bps] + b[dst + y * Bps - 1] - b[dst - Bps - 1]);
                    break;
                case VPred:
                    for (var y = 0; y < size; y++) Buffer.BlockCopy(b, dst - Bps, b, dst + y * Bps, size);
                    break;
                default: // HPred
                    for (var y = 0; y < size; y++) Array.Fill(b, b[dst + y * Bps - 1], dst + y * Bps, size);
                    break;
            }
        }

        private static int Avg2(int a, int b) => (a + b + 1) >> 1;
        private static int Avg3(int a, int b, int c) => (a + 2 * b + c + 2) >> 2;

        private static void PredictSubBlock(byte[] b, int d, int mode)
        {
            // named after the pixels around the block: X is the corner, A–H run along the top, I–L down the left
            int X = b[d - Bps - 1];
            int A = b[d - Bps], B = b[d - Bps + 1], C = b[d - Bps + 2], D = b[d - Bps + 3];
            int E = b[d - Bps + 4], F = b[d - Bps + 5], G = b[d - Bps + 6], H = b[d - Bps + 7];
            int I = b[d - 1], J = b[d - 1 + Bps], K = b[d - 1 + 2 * Bps], L = b[d - 1 + 3 * Bps];

            void Set(int x, int y, int v) => b[d + x + y * Bps] = (byte)v;

            switch (mode)
            {
                case DcPred:
                    var dc = 4;
                    for (var i = 0; i < 4; i++) dc += b[d + i - Bps] + b[d - 1 + i * Bps];
                    dc >>= 3;
                    Fill(b, d, 4, (byte)dc);
                    break;
                case TmPred:
                    for (var y = 0; y < 4; y++)
                        for (var x = 0; x < 4; x++)
                            Set(x, y, Clip(b[d + x - Bps] + b[d + y * Bps - 1] - X));
                    break;
                case VPred:
                    for (var y = 0; y < 4; y++)
                    {
                        Set(0, y, Avg3(X, A, B));
                        Set(1, y, Avg3(A, B, C));
                        Set(2, y, Avg3(B, C, D));
                        Set(3, y, Avg3(C, D, E));
                    }
                    break;
                case HPred:
                    var rows = new[] { Avg3(X, I, J), Avg3(I, J, K), Avg3(J, K, L), Avg3(K, L, L) };
                    for (var y = 0; y < 4; y++) for (var x = 0; x < 4; x++) Set(x, y, rows[y]);
                    break;
                case BRdPred:
                    Set(0, 3, Avg3(J, K, L));
                    Set(1, 3, Avg3(I, J, K)); Set(0, 2, Avg3(I, J, K));
                    Set(2, 3, Avg3(X, I, J)); Set(1, 2, Avg3(X, I, J)); Set(0, 1, Avg3(X, I, J));
                    Set(3, 3, Avg3(A, X, I)); Set(2, 2, Avg3(A, X, I)); Set(1, 1, Avg3(A, X, I)); Set(0, 0, Avg3(A, X, I));
                    Set(3, 2, Avg3(B, A, X)); Set(2, 1, Avg3(B, A, X)); Set(1, 0, Avg3(B, A, X));
                    Set(3, 1, Avg3(C, B, A)); Set(2, 0, Avg3(C, B, A));
                    Set(3, 0, Avg3(D, C, B));
                    break;
                case BVrPred:
                    Set(0, 0, Avg2(X, A)); Set(1, 2, Avg2(X, A));
                    Set(1, 0, Avg2(A, B)); Set(2, 2, Avg2(A, B));
                    Set(2, 0, Avg2(B, C)); Set(3, 2, Avg2(B, C));
                    Set(3, 0, Avg2(C, D));
                    Set(0, 3, Avg3(K, J, I));
                    Set(0, 2, Avg3(J, I, X));
                    Set(0, 1, Avg3(I, X, A)); Set(1, 3, Avg3(I, X, A));
                    Set(1, 1, Avg3(X, A, B)); Set(2, 3, Avg3(X, A, B));
                    Set(2, 1, Avg3(A, B, C)); Set(3, 3, Avg3(A, B, C));
                    Set(3, 1, Avg3(B, C, D));
                    break;
                case BLdPred:
                    Set(0, 0, Avg3(A, B, C));
                    Set(1, 0, Avg3(B, C, D)); Set(0, 1, Avg3(B, C, D));
                    Set(2, 0, Avg3(C, D, E)); Set(1, 1, Avg3(C, D, E)); Set(0, 2, Avg3(C, D, E));
                    Set(3, 0, Avg3(D, E, F)); Set(2, 1, Avg3(D, E, F)); Set(1, 2, Avg3(D, E, F)); Set(0, 3, Avg3(D, E, F));
                    Set(3, 1, Avg3(E, F, G)); Set(2, 2, Avg3(E, F, G)); Set(1, 3, Avg3(E, F, G));
                    Set(3, 2, Avg3(F, G, H)); Set(2, 3, Avg3(F, G, H));
                    Set(3, 3, Avg3(G, H, H));
                    break;
                case BVlPred:
                    Set(0, 0, Avg2(A, B));
                    Set(1, 0, Avg2(B, C)); Set(0, 2, Avg2(B, C));
                    Set(2, 0, Avg2(C, D)); Set(1, 2, Avg2(C, D));
                    Set(3, 0, Avg2(D, E)); Set(2, 2, Avg2(D, E));
                    Set(0, 1, Avg3(A, B, C));
                    Set(1, 1, Avg3(B, C, D)); Set(0, 3, Avg3(B, C, D));
                    Set(2, 1, Avg3(C, D, E)); Set(1, 3, Avg3(C, D, E));
                    Set(3, 1, Avg3(D, E, F)); Set(2, 3, Avg3(D, E, F));
                    Set(3, 2, Avg3(E, F, G));
                    Set(3, 3, Avg3(F, G, H));
                    break;
                case BHdPred:
                    Set(0, 0, Avg2(I, X)); Set(2, 1, Avg2(I, X));
                    Set(0, 1, Avg2(J, I)); Set(2, 2, Avg2(J, I));
                    Set(0, 2, Avg2(K, J)); Set(2, 3, Avg2(K, J));
                    Set(0, 3, Avg2(L, K));
                    Set(3, 0, Avg3(A, B, C));
                    Set(2, 0, Avg3(X, A, B));
                    Set(1, 0, Avg3(I, X, A)); Set(3, 1, Avg3(I, X, A));
                    Set(1, 1, Avg3(J, I, X)); Set(3, 2, Avg3(J, I, X));
                    Set(1, 2, Avg3(K, J, I)); Set(3, 3, Avg3(K, J, I));
                    Set(1, 3, Avg3(L, K, J));
                    break;
                default: // BHuPred
                    Set(0, 0, Avg2(I, J));
                    Set(2, 0, Avg2(J, K)); Set(0, 1, Avg2(J, K));
                    Set(2, 1, Avg2(K, L)); Set(0, 2, Avg2(K, L));
                    Set(1, 0, Avg3(I, J, K));
                    Set(3, 0, Avg3(J, K, L)); Set(1, 1, Avg3(J, K, L));
                    Set(3, 1, Avg3(K, L, L)); Set(1, 2, Avg3(K, L, L));
                    Set(3, 2, L); Set(2, 2, L); Set(0, 3, L); Set(1, 3, L); Set(2, 3, L); Set(3, 3, L);
                    break;
            }
        }

        // ---- loop filter -------------------------------------------------------------------------------------------------------------

        private int FilterType() => _filterLevel == 0 ? 0 : _simpleFilter ? 1 : 2;

        private void Filter()
        {
            var type = FilterType();
            // per segment and prediction kind: the limit, inner-edge threshold and high-edge-variance threshold
            var limits = new int[4, 2];
            var inners = new int[4, 2];
            var hevs = new int[4, 2];
            for (var s = 0; s < 4; s++)
            {
                var baseLevel = _segmentsEnabled ? (_absoluteSegmentValues ? _segmentFilter[s] : _filterLevel + _segmentFilter[s]) : _filterLevel;
                for (var i4 = 0; i4 < 2; i4++)
                {
                    var level = baseLevel;
                    if (_useFilterDeltas)
                    {
                        level += _refDelta[0];
                        if (i4 == 1) level += _modeDelta[0];
                    }
                    level = level < 0 ? 0 : level > 63 ? 63 : level;
                    if (level > 0)
                    {
                        var inner = level;
                        if (_sharpness > 0)
                        {
                            inner >>= _sharpness > 4 ? 2 : 1;
                            if (inner > 9 - _sharpness) inner = 9 - _sharpness;
                        }
                        if (inner < 1) inner = 1;
                        inners[s, i4] = inner;
                        limits[s, i4] = 2 * level + inner;
                        hevs[s, i4] = level >= 40 ? 2 : level >= 15 ? 1 : 0;
                    }
                }
            }

            for (var mbY = 0; mbY < _mbHeight; mbY++)
                for (var mbX = 0; mbX < _mbWidth; mbX++)
                {
                    var index = mbY * _mbWidth + mbX;
                    var s = _mbSegment[index];
                    var i4 = _mbIntra4[index] ? 1 : 0;
                    var limit = limits[s, i4];
                    if (limit == 0) continue;
                    var ilevel = inners[s, i4];
                    var hev = hevs[s, i4];
                    var inner = _mbInner[index];

                    var y = mbY * 16 * _yStride + mbX * 16;
                    var uv = mbY * 8 * _uvStride + mbX * 8;
                    if (type == 1)
                    {
                        if (mbX > 0) SimpleEdge(_yPlane, y, 1, _yStride, limit + 4);
                        if (inner) for (var k = 1; k <= 3; k++) SimpleEdge(_yPlane, y + 4 * k, 1, _yStride, limit);
                        if (mbY > 0) SimpleEdge(_yPlane, y, _yStride, 1, limit + 4);
                        if (inner) for (var k = 1; k <= 3; k++) SimpleEdge(_yPlane, y + 4 * k * _yStride, _yStride, 1, limit);
                    }
                    else
                    {
                        if (mbX > 0)
                        {
                            ComplexEdge(_yPlane, y, 1, _yStride, 16, limit + 4, ilevel, hev, true);
                            ComplexEdge(_uPlane, uv, 1, _uvStride, 8, limit + 4, ilevel, hev, true);
                            ComplexEdge(_vPlane, uv, 1, _uvStride, 8, limit + 4, ilevel, hev, true);
                        }
                        if (inner)
                        {
                            for (var k = 1; k <= 3; k++) ComplexEdge(_yPlane, y + 4 * k, 1, _yStride, 16, limit, ilevel, hev, false);
                            ComplexEdge(_uPlane, uv + 4, 1, _uvStride, 8, limit, ilevel, hev, false);
                            ComplexEdge(_vPlane, uv + 4, 1, _uvStride, 8, limit, ilevel, hev, false);
                        }
                        if (mbY > 0)
                        {
                            ComplexEdge(_yPlane, y, _yStride, 1, 16, limit + 4, ilevel, hev, true);
                            ComplexEdge(_uPlane, uv, _uvStride, 1, 8, limit + 4, ilevel, hev, true);
                            ComplexEdge(_vPlane, uv, _uvStride, 1, 8, limit + 4, ilevel, hev, true);
                        }
                        if (inner)
                        {
                            for (var k = 1; k <= 3; k++) ComplexEdge(_yPlane, y + 4 * k * _yStride, _yStride, 1, 16, limit, ilevel, hev, false);
                            ComplexEdge(_uPlane, uv + 4 * _uvStride, _uvStride, 1, 8, limit, ilevel, hev, false);
                            ComplexEdge(_vPlane, uv + 4 * _uvStride, _uvStride, 1, 8, limit, ilevel, hev, false);
                        }
                    }
                }
        }

        private static int SClip1(int v) => v < -128 ? -128 : v > 127 ? 127 : v;
        private static int SClip2(int v) => v < -16 ? -16 : v > 15 ? 15 : v;

        // "step" runs across the edge (towards the pixels being compared), "along" runs down the edge.

        private static void SimpleEdge(byte[] p, int start, int step, int along, int thresh)
        {
            var t = 2 * thresh + 1;
            for (var i = 0; i < 16; i++)
            {
                var at = start + i * along;
                if (NeedsFilter(p, at, step, t)) DoFilter2(p, at, step);
            }
        }

        private static void ComplexEdge(byte[] p, int start, int step, int along, int size, int thresh, int ithresh, int hevThresh, bool macroblockEdge)
        {
            var t = 2 * thresh + 1;
            for (var i = 0; i < size; i++)
            {
                var at = start + i * along;
                if (!NeedsFilter2(p, at, step, t, ithresh)) continue;
                if (Hev(p, at, step, hevThresh)) DoFilter2(p, at, step);
                else if (macroblockEdge) DoFilter6(p, at, step);
                else DoFilter4(p, at, step);
            }
        }

        private static bool NeedsFilter(byte[] p, int at, int step, int t)
        {
            int p1 = p[at - 2 * step], p0 = p[at - step], q0 = p[at], q1 = p[at + step];
            return 4 * Math.Abs(p0 - q0) + Math.Abs(p1 - q1) <= t;
        }

        private static bool NeedsFilter2(byte[] p, int at, int step, int t, int it)
        {
            int p3 = p[at - 4 * step], p2 = p[at - 3 * step], p1 = p[at - 2 * step], p0 = p[at - step];
            int q0 = p[at], q1 = p[at + step], q2 = p[at + 2 * step], q3 = p[at + 3 * step];
            if (4 * Math.Abs(p0 - q0) + Math.Abs(p1 - q1) > t) return false;
            return Math.Abs(p3 - p2) <= it && Math.Abs(p2 - p1) <= it && Math.Abs(p1 - p0) <= it
                && Math.Abs(q3 - q2) <= it && Math.Abs(q2 - q1) <= it && Math.Abs(q1 - q0) <= it;
        }

        private static bool Hev(byte[] p, int at, int step, int thresh)
        {
            int p1 = p[at - 2 * step], p0 = p[at - step], q0 = p[at], q1 = p[at + step];
            return Math.Abs(p1 - p0) > thresh || Math.Abs(q1 - q0) > thresh;
        }

        private static void DoFilter2(byte[] p, int at, int step)
        {
            int p1 = p[at - 2 * step], p0 = p[at - step], q0 = p[at], q1 = p[at + step];
            var a = 3 * (q0 - p0) + SClip1(p1 - q1);
            var a1 = SClip2((a + 4) >> 3);
            var a2 = SClip2((a + 3) >> 3);
            p[at - step] = Clip(p0 + a2);
            p[at] = Clip(q0 - a1);
        }

        private static void DoFilter4(byte[] p, int at, int step)
        {
            int p1 = p[at - 2 * step], p0 = p[at - step], q0 = p[at], q1 = p[at + step];
            var a = 3 * (q0 - p0);
            var a1 = SClip2((a + 4) >> 3);
            var a2 = SClip2((a + 3) >> 3);
            var a3 = (a1 + 1) >> 1;
            p[at - 2 * step] = Clip(p1 + a3);
            p[at - step] = Clip(p0 + a2);
            p[at] = Clip(q0 - a1);
            p[at + step] = Clip(q1 - a3);
        }

        private static void DoFilter6(byte[] p, int at, int step)
        {
            int p2 = p[at - 3 * step], p1 = p[at - 2 * step], p0 = p[at - step];
            int q0 = p[at], q1 = p[at + step], q2 = p[at + 2 * step];
            var a = SClip1(3 * (q0 - p0) + SClip1(p1 - q1));
            var a1 = (27 * a + 63) >> 7;
            var a2 = (18 * a + 63) >> 7;
            var a3 = (9 * a + 63) >> 7;
            p[at - 3 * step] = Clip(p2 + a3);
            p[at - 2 * step] = Clip(p1 + a2);
            p[at - step] = Clip(p0 + a1);
            p[at] = Clip(q0 - a1);
            p[at + step] = Clip(q1 - a2);
            p[at + 2 * step] = Clip(q2 - a3);
        }

        // ---- YUV → RGBA --------------------------------------------------------------------------------------------------------------

        private static int MultHi(int v, int coefficient) => (v * coefficient) >> 8;

        private static byte Clip8(int v)
        {
            var x = v >> 6;
            return (byte)(x < 0 ? 0 : x > 255 ? 255 : x);
        }

        /// <summary>The chroma value under each luma pixel, interpolated bilinearly (3:1 towards the nearest sample) the way libwebp's "fancy upsampler" does.</summary>
        private static void InterpolateLane(byte[] plane, int topOffset, int curOffset, int length, int[]? top, int[]? bottom)
        {
            var last = (length - 1) >> 1;
            int tl = plane[topOffset], l = plane[curOffset];
            if (top != null) top[0] = (3 * tl + l + 2) >> 2;
            if (bottom != null) bottom[0] = (3 * l + tl + 2) >> 2;
            for (var x = 1; x <= last; x++)
            {
                int t = plane[topOffset + x], c = plane[curOffset + x];
                var avg = tl + t + l + c + 8;
                var diag12 = (avg + 2 * (t + l)) >> 3;
                var diag03 = (avg + 2 * (tl + c)) >> 3;
                if (top != null) { top[2 * x - 1] = (diag12 + tl) >> 1; top[2 * x] = (diag03 + t) >> 1; }
                if (bottom != null) { bottom[2 * x - 1] = (diag03 + l) >> 1; bottom[2 * x] = (diag12 + c) >> 1; }
                tl = t;
                l = c;
            }
            if ((length & 1) == 0)
            {
                if (top != null) top[length - 1] = (3 * tl + l + 2) >> 2;
                if (bottom != null) bottom[length - 1] = (3 * l + tl + 2) >> 2;
            }
        }

        private RgbaImage ToRgba()
        {
            var image = RgbaImage.Create(_width, _height);
            var rgba = image.Pixels;
            var topU = new int[_width]; var topV = new int[_width];
            var botU = new int[_width]; var botV = new int[_width];
            var chromaRows = (_height + 1) >> 1;

            void Row(int yRow, int[] u, int[] v)
            {
                for (var x = 0; x < _width; x++)
                {
                    var y = _yPlane[yRow * _yStride + x];
                    var o = (yRow * _width + x) * 4;
                    rgba[o] = Clip8(MultHi(y, 19077) + MultHi(v[x], 26149) - 14234);
                    rgba[o + 1] = Clip8(MultHi(y, 19077) - MultHi(u[x], 6419) - MultHi(v[x], 13320) + 8708);
                    rgba[o + 2] = Clip8(MultHi(y, 19077) + MultHi(u[x], 33050) - 17685);
                    rgba[o + 3] = 255;
                }
            }

            // row 0 sits on chroma row 0 alone; after that each pair of luma rows lies between two chroma rows
            InterpolateLane(_uPlane, 0, 0, _width, topU, null);
            InterpolateLane(_vPlane, 0, 0, _width, topV, null);
            Row(0, topU, topV);

            var r = 1;
            for (; r + 1 < _height; r += 2)
            {
                var k = (r + 1) >> 1;
                InterpolateLane(_uPlane, (k - 1) * _uvStride, k * _uvStride, _width, topU, botU);
                InterpolateLane(_vPlane, (k - 1) * _uvStride, k * _uvStride, _width, topV, botV);
                Row(r, topU, topV);
                Row(r + 1, botU, botV);
            }
            if (r < _height) // an even height leaves the last row alone, mirrored onto the last chroma row
            {
                var last = (chromaRows - 1) * _uvStride;
                InterpolateLane(_uPlane, last, last, _width, topU, null);
                InterpolateLane(_vPlane, last, last, _width, topV, null);
                Row(r, topU, topV);
            }
            return image;
        }
    }
}
