using System.Buffers.Binary;

namespace Shan.ImageCompressor.Jpeg;

/// <summary>
/// Decodes baseline, extended-sequential and progressive, 8-bit, Huffman-coded JPEGs (grayscale or 3-component YCbCr/RGB, any chroma
/// subsampling, restart intervals, interleaved or per-component scans). Lossless, arithmetic-coded and CMYK files are rejected.
/// </summary>
internal sealed class JpegDecoder
{
    private sealed class Component
    {
        public int Id;
        public int H;
        public int V;
        public int QuantIndex;
        public int BlocksPerLine;   // padded to whole MCUs
        public int BlocksPerColumn; // padded to whole MCUs
        public int Width;           // actual sample width of this component
        public int Height;
        public byte[] Plane = [];
        public short[] Coeffs = []; // progressive only: every block's coefficients, natural order, filled in scan by scan
        public int[]? Quant;        // progressive only: the table in force when the component's first scan arrived
        public int PlaneWidth;
        public int DcTable;
        public int AcTable;
        public int Pred;
    }

    private static readonly float[] IdctTable = BuildIdctTable();

    private readonly byte[] _data;
    private readonly long _maxPixels;
    private readonly int[]?[] _quant = new int[4][];
    private readonly JpegHuffmanTable?[] _dc = new JpegHuffmanTable[4];
    private readonly JpegHuffmanTable?[] _ac = new JpegHuffmanTable[4];
    private Component[] _components = [];
    private int _width;
    private int _height;
    private int _hMax;
    private int _vMax;
    private int _mcusX;
    private int _mcusY;
    private int _restartInterval;
    private int _adobeTransform = -1;
    private bool _scanned;
    private int _orientation = 1;
    private bool _orientationRead;
    private int _pos;
    private bool _progressive;
    private byte[]? _exif;
    private readonly SortedDictionary<int, byte[]> _iccChunks = [];
    private int _iccChunkCount;
    private int _eobrun;

    private JpegDecoder(byte[] data, long maxPixels)
    {
        _data = data;
        _maxPixels = maxPixels;
    }

    /// <summary>Decodes <paramref name="data"/> to RGBA. <paramref name="orientation"/> is the EXIF orientation (1–8); the pixels are NOT rotated.</summary>
    public static RgbaImage Decode(byte[] data, long maxPixels, out int orientation)
    {
        var decoder = new JpegDecoder(data, maxPixels);
        try
        {
            var image = decoder.DecodeCore();
            orientation = decoder._orientation;
            return image;
        }
        catch (Exception e) when (e is IndexOutOfRangeException or ArgumentException or OverflowException)
        {
            throw new InvalidDataException("The JPEG data is corrupt.", e);
        }
    }

    private RgbaImage DecodeCore()
    {
        if (_data.Length < 4 || _data[0] != 0xFF || _data[1] != 0xD8) throw new InvalidDataException("Not a JPEG.");
        _pos = 2;

        while (_pos < _data.Length)
        {
            if (_data[_pos] != 0xFF) { _pos++; continue; }
            while (_pos < _data.Length && _data[_pos] == 0xFF) _pos++;
            if (_pos >= _data.Length) break;
            var marker = _data[_pos++];

            if (marker == 0x00 || marker == 0x01 || marker == 0xD8 || marker is >= 0xD0 and <= 0xD7) continue;
            if (marker == 0xD9) break;

            if (_pos + 2 > _data.Length) throw new InvalidDataException("The JPEG data is truncated.");
            var length = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(_pos));
            if (length < 2 || _pos + length > _data.Length) throw new InvalidDataException("The JPEG data is truncated.");
            var body = _data.AsSpan(_pos + 2, length - 2);

            switch (marker)
            {
                case 0xC0 or 0xC1: ReadFrame(body, false); break;
                case 0xC2: ReadFrame(body, true); break;
                case 0xC3 or 0xC5 or 0xC6 or 0xC7 or 0xC9 or 0xCA or 0xCB or 0xCD or 0xCE or 0xCF:
                    throw new NotSupportedException("This JPEG coding process (lossless, hierarchical or arithmetic) is not supported.");
                case 0xC4: ReadHuffmanTables(body); break;
                case 0xDB: ReadQuantTables(body); break;
                case 0xDD: _restartInterval = BinaryPrimitives.ReadUInt16BigEndian(body); break;
                case 0xE1:
                    if (!_orientationRead) { _orientation = ExifOrientation.Read(body); _orientationRead = true; }
                    if (_exif == null && body.Length > 14 && body[..6].SequenceEqual("Exif\0\0"u8)) _exif = body[6..].ToArray();
                    break;
                case 0xE2:
                    if (body.Length > 14 && body[..12].SequenceEqual("ICC_PROFILE\0"u8) && body[12] >= 1 && body[12] <= body[13])
                    {
                        _iccChunks[body[12]] = body[14..].ToArray();
                        _iccChunkCount = body[13];
                    }
                    break;
                case 0xEE:
                    if (body.Length >= 12 && body[..5].SequenceEqual("Adobe"u8)) _adobeTransform = body[11];
                    break;
                case 0xDA:
                    _pos += length;
                    DecodeScan(body);
                    continue;
            }
            _pos += length;
        }

        if (_components.Length == 0 || !_scanned) throw new InvalidDataException("The JPEG has no image data.");
        if (_progressive) FinalizeProgressive();
        var image = BuildImage();
        image.Metadata = BuildMetadata();
        return image;
    }

    private void ReadFrame(ReadOnlySpan<byte> body, bool progressive)
    {
        if (_components.Length != 0) throw new InvalidDataException("The JPEG has more than one frame.");
        if (body.Length < 6) throw new InvalidDataException("The JPEG frame header is truncated.");
        if (body[0] != 8) throw new NotSupportedException("Only 8-bit JPEGs are supported.");

        _height = BinaryPrimitives.ReadUInt16BigEndian(body[1..]);
        _width = BinaryPrimitives.ReadUInt16BigEndian(body[3..]);
        var count = body[5];
        if (_width == 0 || _height == 0) throw new InvalidDataException("The JPEG has invalid dimensions.");
        if ((long)_width * _height > _maxPixels) throw new InvalidDataException($"The image has {(long)_width * _height} pixels, over the {_maxPixels} limit.");
        if (count is not (1 or 3 or 4)) throw new NotSupportedException("Only grayscale, 3-component (YCbCr/RGB) and 4-component (CMYK/YCCK) JPEGs are supported.");
        if (body.Length < 6 + count * 3) throw new InvalidDataException("The JPEG frame header is truncated.");

        var components = new Component[count];
        for (var i = 0; i < count; i++)
        {
            var c = new Component { Id = body[6 + i * 3], H = body[7 + i * 3] >> 4, V = body[7 + i * 3] & 15, QuantIndex = body[8 + i * 3] };
            if (c.H is < 1 or > 4 || c.V is < 1 or > 4 || c.QuantIndex > 3) throw new InvalidDataException("The JPEG has an invalid component definition.");
            components[i] = c;
        }

        _progressive = progressive;
        _hMax = components.Max(c => c.H);
        _vMax = components.Max(c => c.V);
        _mcusX = (_width + 8 * _hMax - 1) / (8 * _hMax);
        _mcusY = (_height + 8 * _vMax - 1) / (8 * _vMax);

        foreach (var c in components)
        {
            c.BlocksPerLine = _mcusX * c.H;
            c.BlocksPerColumn = _mcusY * c.V;
            c.Width = (_width * c.H + _hMax - 1) / _hMax;
            c.Height = (_height * c.V + _vMax - 1) / _vMax;
            c.PlaneWidth = c.BlocksPerLine * 8;
            if (progressive) c.Coeffs = new short[checked(c.BlocksPerLine * c.BlocksPerColumn * 64)];
            else c.Plane = new byte[checked(c.PlaneWidth * c.BlocksPerColumn * 8)];
        }
        _components = components;
    }

    private void ReadQuantTables(ReadOnlySpan<byte> body)
    {
        var i = 0;
        while (i < body.Length)
        {
            var precision = body[i] >> 4;
            var id = body[i] & 15;
            i++;
            if (id > 3) throw new InvalidDataException("The JPEG has an invalid quantisation table id.");
            var table = new int[64];
            for (var k = 0; k < 64; k++)
            {
                table[k] = precision == 0 ? body[i] : BinaryPrimitives.ReadUInt16BigEndian(body[i..]);
                i += precision == 0 ? 1 : 2;
            }
            _quant[id] = table; // stored in zigzag order, exactly as in the file
        }
    }

    private void ReadHuffmanTables(ReadOnlySpan<byte> body)
    {
        var i = 0;
        while (i + 17 <= body.Length)
        {
            var kind = body[i] >> 4;
            var id = body[i] & 15;
            var counts = body.Slice(i + 1, 16);
            var total = 0;
            foreach (var n in counts) total += n;
            i += 17;
            if (id > 3 || kind > 1 || total > 256 || i + total > body.Length) throw new InvalidDataException("The JPEG has an invalid Huffman table.");

            var table = new JpegHuffmanTable(counts, body.Slice(i, total).ToArray());
            if (kind == 0) _dc[id] = table; else _ac[id] = table;
            i += total;
        }
    }

    private void DecodeScan(ReadOnlySpan<byte> header)
    {
        if (_components.Length == 0) throw new InvalidDataException("The JPEG scan appears before its frame header.");
        var count = header[0];
        if (count < 1 || count > _components.Length || header.Length < 1 + count * 2) throw new InvalidDataException("The JPEG scan header is invalid.");

        var scan = new Component[count];
        for (var i = 0; i < count; i++)
        {
            var id = header[1 + i * 2];
            var c = _components.FirstOrDefault(x => x.Id == id) ?? throw new InvalidDataException("The JPEG scan refers to an unknown component.");
            c.DcTable = header[2 + i * 2] >> 4;
            c.AcTable = header[2 + i * 2] & 15;
            if (c.DcTable > 3 || c.AcTable > 3) throw new InvalidDataException("The JPEG scan refers to an invalid Huffman table.");
            scan[i] = c;
        }

        int ss = 0, se = 63, ah = 0, al = 0;
        if (_progressive)
        {
            if (header.Length < 1 + count * 2 + 3) throw new InvalidDataException("The JPEG scan header is invalid.");
            ss = header[1 + count * 2];
            se = header[2 + count * 2];
            ah = header[3 + count * 2] >> 4;
            al = header[3 + count * 2] & 15;
            var validBand = ss == 0 ? se == 0 : se >= ss && se <= 63 && count == 1;
            if (!validBand || al > 13) throw new InvalidDataException("The JPEG progressive scan parameters are invalid.");
            foreach (var c in scan) c.Quant ??= _quant[c.QuantIndex];
        }

        var reader = new JpegBitReader(_data, _pos);
        var coefficients = new int[64];
        _eobrun = 0;
        foreach (var c in scan) c.Pred = 0;

        int totalMcus, blocksAcross = 0;
        if (count == 1)
        {
            blocksAcross = (scan[0].Width + 7) / 8;
            totalMcus = blocksAcross * ((scan[0].Height + 7) / 8);
        }
        else totalMcus = _mcusX * _mcusY;

        for (var mcu = 0; mcu < totalMcus; mcu++)
        {
            if (_restartInterval > 0 && mcu > 0 && mcu % _restartInterval == 0)
            {
                reader.Restart();
                _eobrun = 0;
                foreach (var c in scan) c.Pred = 0;
            }

            if (count == 1)
            {
                var bx = mcu % blocksAcross;
                var by = mcu / blocksAcross;
                if (_progressive) DecodeProgressiveBlock(reader, scan[0], bx, by, ss, se, ah, al);
                else DecodeBlock(reader, scan[0], bx, by, coefficients);
            }
            else
            {
                var mx = mcu % _mcusX;
                var my = mcu / _mcusX;
                foreach (var c in scan)
                    for (var v = 0; v < c.V; v++)
                        for (var h = 0; h < c.H; h++)
                        {
                            if (_progressive) DecodeProgressiveBlock(reader, c, mx * c.H + h, my * c.V + v, ss, se, ah, al);
                            else DecodeBlock(reader, c, mx * c.H + h, my * c.V + v, coefficients);
                        }
            }
        }

        _pos = reader.Position;
        _scanned = true;
    }

    private void DecodeBlock(JpegBitReader reader, Component c, int blockX, int blockY, int[] coefficients)
    {
        var dc = _dc[c.DcTable] ?? throw new InvalidDataException("The JPEG is missing a Huffman table.");
        var ac = _ac[c.AcTable] ?? throw new InvalidDataException("The JPEG is missing a Huffman table.");
        var quant = _quant[c.QuantIndex] ?? throw new InvalidDataException("The JPEG is missing a quantisation table.");

        Array.Clear(coefficients);
        c.Pred += reader.ReceiveExtend(reader.Decode(dc));
        coefficients[0] = c.Pred * quant[0];

        var acPresent = false;
        for (var k = 1; k < 64;)
        {
            var rs = reader.Decode(ac);
            var run = rs >> 4;
            var size = rs & 15;
            if (size == 0)
            {
                if (run != 15) break;
                k += 16;
                continue;
            }
            k += run;
            if (k > 63) throw new InvalidDataException("The JPEG has a corrupt coefficient run.");
            coefficients[JpegTables.Zigzag[k]] = reader.ReceiveExtend(size) * quant[k];
            acPresent = true;
            k++;
        }

        Idct(coefficients, acPresent, c.Plane, c.PlaneWidth, blockX * 8, blockY * 8);
    }

    /// <summary>One block of one progressive scan: a DC first/refinement pass or an AC first/refinement pass over the band ss..se (ITU T.81 G.1.2).</summary>
    private void DecodeProgressiveBlock(JpegBitReader reader, Component c, int blockX, int blockY, int ss, int se, int ah, int al)
    {
        var coefs = c.Coeffs;
        var offset = (blockY * c.BlocksPerLine + blockX) * 64;

        if (ss == 0)
        {
            if (ah == 0)
            {
                var dc = _dc[c.DcTable] ?? throw new InvalidDataException("The JPEG is missing a Huffman table.");
                c.Pred += reader.ReceiveExtend(reader.Decode(dc));
                coefs[offset] = (short)(c.Pred << al);
            }
            else if (reader.ReadBit() != 0) coefs[offset] |= (short)(1 << al);
            return;
        }

        var ac = _ac[c.AcTable] ?? throw new InvalidDataException("The JPEG is missing a Huffman table.");
        if (ah == 0) DecodeAcFirst(reader, ac, coefs, offset, ss, se, al);
        else DecodeAcRefine(reader, ac, coefs, offset, ss, se, al);
    }

    private void DecodeAcFirst(JpegBitReader reader, JpegHuffmanTable ac, short[] coefs, int offset, int ss, int se, int al)
    {
        if (_eobrun > 0)
        {
            _eobrun--;
            return;
        }

        for (var k = ss; k <= se; k++)
        {
            var rs = reader.Decode(ac);
            var run = rs >> 4;
            var size = rs & 15;
            if (size == 0)
            {
                if (run < 15)
                {
                    _eobrun = (1 << run) - 1; // this block is the first of the run
                    if (run > 0) _eobrun += reader.ReadBits(run);
                    break;
                }
                k += 15; // ZRL: sixteen zeros, the loop's own increment adds the last one
                continue;
            }
            k += run;
            if (k > 63) throw new InvalidDataException("The JPEG has a corrupt coefficient run.");
            coefs[offset + JpegTables.Zigzag[k]] = (short)(reader.ReceiveExtend(size) * (1 << al));
        }
    }

    /// <summary>Adds one more bit of precision: nudges the coefficients already known and slots in the newly non-zero ones.</summary>
    private void DecodeAcRefine(JpegBitReader reader, JpegHuffmanTable ac, short[] coefs, int offset, int ss, int se, int al)
    {
        var p1 = 1 << al;
        var m1 = -1 << al;
        var k = ss;

        if (_eobrun == 0)
        {
            for (; k <= se; k++)
            {
                var rs = reader.Decode(ac);
                var run = rs >> 4;
                var value = 0;
                if ((rs & 15) != 0) value = reader.ReadBit() != 0 ? p1 : m1;
                else if (run != 15)
                {
                    _eobrun = 1 << run;
                    if (run > 0) _eobrun += reader.ReadBits(run);
                    break;
                }

                while (k <= se) // step over `run` zero coefficients, correcting each non-zero one passed on the way
                {
                    ref var coef = ref coefs[offset + JpegTables.Zigzag[k]];
                    if (coef != 0) RefineCoefficient(ref coef, reader.ReadBit(), p1, m1);
                    else if (--run < 0) break;
                    k++;
                }

                if (value != 0)
                {
                    if (k > se) throw new InvalidDataException("The JPEG has a corrupt coefficient run.");
                    coefs[offset + JpegTables.Zigzag[k]] = (short)value;
                }
            }
        }

        if (_eobrun > 0)
        {
            for (; k <= se; k++)
            {
                ref var coef = ref coefs[offset + JpegTables.Zigzag[k]];
                if (coef != 0) RefineCoefficient(ref coef, reader.ReadBit(), p1, m1);
            }
            _eobrun--;
        }
    }

    private static void RefineCoefficient(ref short coef, int bit, int p1, int m1)
    {
        if (bit != 0 && (coef & p1) == 0) coef += (short)(coef >= 0 ? p1 : m1);
    }

    /// <summary>Dequantises and inverse-transforms every block once all progressive scans have been read.</summary>
    private void FinalizeProgressive()
    {
        var coefficients = new int[64];
        foreach (var c in _components)
        {
            var quant = c.Quant ?? _quant[c.QuantIndex] ?? throw new InvalidDataException("The JPEG is missing a quantisation table.");
            var natural = new int[64];
            for (var k = 0; k < 64; k++) natural[JpegTables.Zigzag[k]] = quant[k];

            c.Plane = new byte[checked(c.PlaneWidth * c.BlocksPerColumn * 8)];
            for (var by = 0; by < c.BlocksPerColumn; by++)
            {
                for (var bx = 0; bx < c.BlocksPerLine; bx++)
                {
                    var offset = (by * c.BlocksPerLine + bx) * 64;
                    var acPresent = false;
                    for (var i = 0; i < 64; i++)
                    {
                        var v = c.Coeffs[offset + i];
                        coefficients[i] = v * natural[i];
                        if (i > 0 && v != 0) acPresent = true;
                    }
                    Idct(coefficients, acPresent, c.Plane, c.PlaneWidth, bx * 8, by * 8);
                }
            }
            c.Coeffs = [];
        }
    }

    private static float[] BuildIdctTable()
    {
        var table = new float[64]; // [frequency u][sample x]
        for (var u = 0; u < 8; u++)
            for (var x = 0; x < 8; x++)
                table[u * 8 + x] = (float)((u == 0 ? Math.Sqrt(0.125) : 0.5) * Math.Cos((2 * x + 1) * u * Math.PI / 16));
        return table;
    }

    private static void Idct(int[] coefficients, bool acPresent, byte[] plane, int stride, int x0, int y0)
    {
        if (!acPresent)
        {
            var flat = ClampByte(coefficients[0] / 8f + 128f);
            for (var y = 0; y < 8; y++)
                Array.Fill(plane, flat, (y0 + y) * stride + x0, 8);
            return;
        }

        Span<float> temp = stackalloc float[64];
        for (var v = 0; v < 8; v++) // horizontal pass: every row of coefficients → samples along x
        {
            for (var x = 0; x < 8; x++)
            {
                float sum = 0;
                for (var u = 0; u < 8; u++)
                {
                    var c = coefficients[v * 8 + u];
                    if (c != 0) sum += IdctTable[u * 8 + x] * c;
                }
                temp[v * 8 + x] = sum;
            }
        }

        for (var x = 0; x < 8; x++) // vertical pass
        {
            for (var y = 0; y < 8; y++)
            {
                float sum = 0;
                for (var v = 0; v < 8; v++) sum += IdctTable[v * 8 + y] * temp[v * 8 + x];
                plane[(y0 + y) * stride + x0 + x] = ClampByte(sum + 128f);
            }
        }
    }

    private static byte ClampByte(float v) => v <= 0 ? (byte)0 : v >= 255f ? (byte)255 : (byte)(v + 0.5f);

    private ImageMetadata? BuildMetadata()
    {
        byte[]? icc = null;
        if (_iccChunkCount > 0 && _iccChunks.Count == _iccChunkCount && _iccChunks.Keys.SequenceEqual(Enumerable.Range(1, _iccChunkCount)))
            icc = _iccChunks.Values.SelectMany(c => c).ToArray();
        return _exif == null && icc == null ? null : new ImageMetadata(_exif, icc);
    }

    private RgbaImage BuildImage()
    {
        var image = RgbaImage.Create(_width, _height);
        var pixels = image.Pixels;
        var planes = _components.Select(Upsample).ToArray();
        var rgb = _components.Length == 3 && _adobeTransform == 0;

        for (var i = 0; i < _width * _height; i++)
        {
            var o = i * 4;
            if (planes.Length == 4) BuildCmykPixel(pixels, o, planes, i);
            else if (planes.Length == 1) pixels[o] = pixels[o + 1] = pixels[o + 2] = planes[0][i];
            else
            {
                float a = planes[0][i], b = planes[1][i], c = planes[2][i];
                if (rgb) { pixels[o] = (byte)a; pixels[o + 1] = (byte)b; pixels[o + 2] = (byte)c; }
                else
                {
                    pixels[o] = ClampByte(a + 1.402f * (c - 128f));
                    pixels[o + 1] = ClampByte(a - 0.344136f * (b - 128f) - 0.714136f * (c - 128f));
                    pixels[o + 2] = ClampByte(a + 1.772f * (b - 128f));
                }
            }
            pixels[o + 3] = 255;
        }
        return image;
    }

    /// <summary>
    /// One CMYK/YCCK pixel to RGB. Adobe writes CMYK inverted (255 = no ink), so with an Adobe marker the stored values multiply directly;
    /// YCCK first turns its YCC triple into those stored values, as libjpeg does. Without a marker the values are plain ink amounts.
    /// </summary>
    private void BuildCmykPixel(byte[] pixels, int o, byte[][] planes, int i)
    {
        int c, m, y, k = planes[3][i];
        if (_adobeTransform == 2)
        {
            float a = planes[0][i], b = planes[1][i], cr = planes[2][i];
            c = 255 - ClampByte(a + 1.402f * (cr - 128f));
            m = 255 - ClampByte(a - 0.344136f * (b - 128f) - 0.714136f * (cr - 128f));
            y = 255 - ClampByte(a + 1.772f * (b - 128f));
        }
        else { c = planes[0][i]; m = planes[1][i]; y = planes[2][i]; }

        if (_adobeTransform < 0) { c = 255 - c; m = 255 - m; y = 255 - y; k = 255 - k; } // no Adobe marker: not inverted
        pixels[o] = (byte)(c * k / 255);
        pixels[o + 1] = (byte)(m * k / 255);
        pixels[o + 2] = (byte)(y * k / 255);
    }

    /// <summary>
    /// A component's samples at full image size. A subsampled component is interpolated linearly between sample centres
    /// (the "fancy upsampling" libjpeg uses), so chroma edges stay smooth instead of blocky.
    /// </summary>
    private byte[] Upsample(Component c)
    {
        var result = new byte[_width * _height];
        var (x0, x1, xw) = Taps(_width, c.H, _hMax, c.Width);
        var (y0, y1, yw) = Taps(_height, c.V, _vMax, c.Height);

        for (var y = 0; y < _height; y++)
        {
            var rowA = y0[y] * c.PlaneWidth;
            var rowB = y1[y] * c.PlaneWidth;
            var wy = yw[y];
            for (var x = 0; x < _width; x++)
            {
                var wx = xw[x];
                var top = c.Plane[rowA + x0[x]] * (256 - wx) + c.Plane[rowA + x1[x]] * wx;
                var bottom = c.Plane[rowB + x0[x]] * (256 - wx) + c.Plane[rowB + x1[x]] * wx;
                result[y * _width + x] = (byte)((top * (256 - wy) + bottom * wy + 32768) >> 16);
            }
        }
        return result;
    }

    /// <summary>For each output position: the two neighbouring source samples and the weight (0–256) of the second.</summary>
    private static (int[] First, int[] Second, int[] Weight) Taps(int outputLength, int factor, int maxFactor, int sourceLength)
    {
        var first = new int[outputLength];
        var second = new int[outputLength];
        var weight = new int[outputLength];
        for (var i = 0; i < outputLength; i++)
        {
            if (factor == maxFactor) { first[i] = second[i] = i; continue; }
            var position = (i + 0.5) * factor / maxFactor - 0.5; // source coordinate of this output pixel's centre
            var a = (int)Math.Floor(position);
            first[i] = Math.Clamp(a, 0, sourceLength - 1);
            second[i] = Math.Clamp(a + 1, 0, sourceLength - 1);
            weight[i] = (int)Math.Round((position - a) * 256);
        }
        return (first, second, weight);
    }
}
