using System.Buffers.Binary;

namespace Shan.ImageCompressor.Jpeg;

/// <summary>Writes a baseline JPEG (JFIF, standard Huffman tables). 4:2:0 chroma below quality 90, 4:4:4 from 90 up.</summary>
internal static class JpegEncoder
{
    /// <summary>AAN forward-DCT output scale per frequency: √2·cos(kπ/16)·2√2 (k = 0 uses 1·2√2).</summary>
    private static readonly float[] DctScale = BuildDctScale();

    private static float[] BuildDctScale()
    {
        var scale = new float[8];
        for (var k = 0; k < 8; k++)
            scale[k] = (float)((k == 0 ? 1.0 : Math.Sqrt(2) * Math.Cos(k * Math.PI / 16)) * 2 * Math.Sqrt(2));
        return scale;
    }

    public const int MaxDimension = 65535;

    public static MemoryStream Encode(RgbaImage image, int quality, ImageMetadata? metadata = null)
    {
        if (image.Width > MaxDimension || image.Height > MaxDimension) throw new NotSupportedException("JPEG cannot hold an image over 65535 pixels on a side.");
        quality = Math.Clamp(quality, 1, 100);
        var subsample = quality < 90;
        var mcu = subsample ? 16 : 8;
        var mcusX = (image.Width + mcu - 1) / mcu;
        var mcusY = (image.Height + mcu - 1) / mcu;
        var planeW = mcusX * mcu;
        var planeH = mcusY * mcu;
        var chromaW = subsample ? planeW / 2 : planeW;
        var chromaH = subsample ? planeH / 2 : planeH;

        var yPlane = new byte[planeW * planeH];
        var cbPlane = new byte[chromaW * chromaH];
        var crPlane = new byte[chromaW * chromaH];
        ConvertToYCbCr(image, subsample, planeW, chromaW, chromaH, yPlane, cbPlane, crPlane);

        var lumaQuant = ScaleQuant(JpegTables.LuminanceQuant, quality);
        var chromaQuant = ScaleQuant(JpegTables.ChrominanceQuant, quality);
        var lumaInverse = InverseQuant(lumaQuant);
        var chromaInverse = InverseQuant(chromaQuant);

        var dcLuma = new HuffmanEncodeTable(JpegTables.DcLuminanceCounts, JpegTables.DcValues);
        var dcChroma = new HuffmanEncodeTable(JpegTables.DcChrominanceCounts, JpegTables.DcValues);
        var acLuma = new HuffmanEncodeTable(JpegTables.AcLuminanceCounts, JpegTables.AcLuminanceValues);
        var acChroma = new HuffmanEncodeTable(JpegTables.AcChrominanceCounts, JpegTables.AcChrominanceValues);

        var output = new MemoryStream();
        WriteHeaders(output, image.Width, image.Height, subsample, lumaQuant, chromaQuant, metadata);

        // The DCT and quantisation of every block are independent, so they run across cores; only the Huffman stage is sequential.
        var yBlocksX = planeW / 8;
        var cBlocksX = chromaW / 8;
        var yCoefs = Quantise(yPlane, planeW, planeH, lumaInverse);
        var cbCoefs = Quantise(cbPlane, chromaW, chromaH, chromaInverse);
        var crCoefs = Quantise(crPlane, chromaW, chromaH, chromaInverse);

        var bits = new JpegBitWriter(output);
        int predY = 0, predCb = 0, predCr = 0;

        for (var my = 0; my < mcusY; my++)
        {
            for (var mx = 0; mx < mcusX; mx++)
            {
                if (subsample)
                {
                    for (var by = 0; by < 2; by++)
                        for (var bx = 0; bx < 2; bx++)
                            EmitBlock(yCoefs, ((my * 2 + by) * yBlocksX + mx * 2 + bx) * 64, ref predY, dcLuma, acLuma, bits);
                }
                else EmitBlock(yCoefs, (my * yBlocksX + mx) * 64, ref predY, dcLuma, acLuma, bits);

                // One chroma block per MCU either way: an 8×8 block covers a 16×16 luma area when subsampled, 8×8 otherwise.
                EmitBlock(cbCoefs, (my * cBlocksX + mx) * 64, ref predCb, dcChroma, acChroma, bits);
                EmitBlock(crCoefs, (my * cBlocksX + mx) * 64, ref predCr, dcChroma, acChroma, bits);
            }
        }

        bits.Flush();
        output.WriteByte(0xFF);
        output.WriteByte(0xD9);
        output.Position = 0;
        return output;
    }

    private static void ConvertToYCbCr(RgbaImage image, bool subsample, int planeW, int chromaW, int chromaH, byte[] yPlane, byte[] cbPlane, byte[] crPlane)
    {
        var w = image.Width;
        var h = image.Height;
        var px = image.Pixels;
        var step = subsample ? 2 : 1;

        Work.Rows(chromaH, cy =>
        {
            for (var cx = 0; cx < chromaW; cx++)
            {
                float cbSum = 0, crSum = 0;
                for (var dy = 0; dy < step; dy++)
                {
                    for (var dx = 0; dx < step; dx++)
                    {
                        var x = cx * step + dx;
                        var y = cy * step + dy;
                        var o = (Math.Min(y, h - 1) * w + Math.Min(x, w - 1)) * 4; // padding replicates the edge pixel
                        float a = px[o + 3];
                        float r = Flatten(px[o], a), g = Flatten(px[o + 1], a), b = Flatten(px[o + 2], a);
                        yPlane[y * planeW + x] = ClampByte(0.299f * r + 0.587f * g + 0.114f * b);
                        cbSum += -0.168736f * r - 0.331264f * g + 0.5f * b + 128f;
                        crSum += 0.5f * r - 0.418688f * g - 0.081312f * b + 128f;
                    }
                }
                var n = step * step;
                cbPlane[cy * chromaW + cx] = ClampByte(cbSum / n);
                crPlane[cy * chromaW + cx] = ClampByte(crSum / n);
            }
        });
    }

    /// <summary>Composites a straight-alpha channel over white, since JPEG has nowhere to keep alpha.</summary>
    private static float Flatten(byte channel, float alpha) => alpha >= 255f ? channel : (channel * alpha + 255f * (255f - alpha)) / 255f;

    private static byte ClampByte(float v) => v <= 0 ? (byte)0 : v >= 255f ? (byte)255 : (byte)(v + 0.5f);

    private static int[] ScaleQuant(byte[] baseTable, int quality)
    {
        var scale = quality < 50 ? 5000 / quality : 200 - quality * 2;
        var table = new int[64];
        for (var i = 0; i < 64; i++)
            table[i] = Math.Clamp((baseTable[i] * scale + 50) / 100, 1, 255);
        return table;
    }

    /// <summary>Reciprocals that also undo the AAN DCT's output scaling, so quantising is one multiply.</summary>
    private static float[] InverseQuant(int[] quant)
    {
        var inverse = new float[64];
        for (var v = 0; v < 8; v++)
            for (var u = 0; u < 8; u++)
                inverse[v * 8 + u] = 1f / (quant[v * 8 + u] * DctScale[v] * DctScale[u]);
        return inverse;
    }

    private static void WriteHeaders(MemoryStream o, int width, int height, bool subsample, int[] lumaQuant, int[] chromaQuant, ImageMetadata? metadata)
    {
        o.Write([0xFF, 0xD8]);
        o.Write([0xFF, 0xE0, 0, 16, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1, 1, 0, 0, 1, 0, 1, 0, 0]);
        WriteMetadata(o, metadata);

        o.Write([0xFF, 0xDB, 0, 132]);
        foreach (var (id, table) in new[] { (0, lumaQuant), (1, chromaQuant) })
        {
            o.WriteByte((byte)id);
            for (var i = 0; i < 64; i++) o.WriteByte((byte)table[JpegTables.Zigzag[i]]);
        }

        Span<byte> dims = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(dims, (ushort)height);
        BinaryPrimitives.WriteUInt16BigEndian(dims[2..], (ushort)width);
        o.Write([0xFF, 0xC0, 0, 17, 8]);
        o.Write(dims);
        o.Write([3, 1, subsample ? (byte)0x22 : (byte)0x11, 0, 2, 0x11, 1, 3, 0x11, 1]);

        WriteHuffman(o, 0x00, JpegTables.DcLuminanceCounts, JpegTables.DcValues);
        WriteHuffman(o, 0x10, JpegTables.AcLuminanceCounts, JpegTables.AcLuminanceValues);
        WriteHuffman(o, 0x01, JpegTables.DcChrominanceCounts, JpegTables.DcValues);
        WriteHuffman(o, 0x11, JpegTables.AcChrominanceCounts, JpegTables.AcChrominanceValues);

        o.Write([0xFF, 0xDA, 0, 12, 3, 1, 0x00, 2, 0x11, 3, 0x11, 0, 63, 0]);
    }

    /// <summary>EXIF (APP1) and ICC profile (APP2, split into 64 KB chunks) segments. An EXIF block too big for one segment is dropped.</summary>
    private static void WriteMetadata(MemoryStream o, ImageMetadata? metadata)
    {
        if (metadata == null) return;

        if (metadata.Exif is { Length: > 0 } exif && exif.Length <= 65535 - 2 - 6)
        {
            var length = exif.Length + 6 + 2;
            o.Write([0xFF, 0xE1, (byte)(length >> 8), (byte)length]);
            o.Write("Exif\0\0"u8);
            o.Write(exif);
        }

        if (metadata.IccProfile is { Length: > 0 } icc)
        {
            const int chunkSize = 65535 - 2 - 14;
            var chunks = (icc.Length + chunkSize - 1) / chunkSize;
            if (chunks > 255) return;
            for (var i = 0; i < chunks; i++)
            {
                var part = icc.AsSpan(i * chunkSize, Math.Min(chunkSize, icc.Length - i * chunkSize));
                var length = part.Length + 14 + 2;
                o.Write([0xFF, 0xE2, (byte)(length >> 8), (byte)length]);
                o.Write("ICC_PROFILE\0"u8);
                o.WriteByte((byte)(i + 1));
                o.WriteByte((byte)chunks);
                o.Write(part);
            }
        }
    }

    private static void WriteHuffman(MemoryStream o, byte classAndId, byte[] counts, byte[] values)
    {
        var length = 2 + 1 + 16 + values.Length;
        o.Write([0xFF, 0xC4, (byte)(length >> 8), (byte)length, classAndId]);
        o.Write(counts);
        o.Write(values);
    }

    /// <summary>DCT and quantise every 8×8 block of a plane; coefficients are stored per block, 64 shorts each, in zigzag order.</summary>
    private static short[] Quantise(byte[] plane, int width, int height, float[] inverseQuant)
    {
        var blocksX = width / 8;
        var blocksY = height / 8;
        var result = new short[blocksX * blocksY * 64];
        Work.Rows(blocksY, () => new float[64], (by, block) =>
        {
            for (var bx = 0; bx < blocksX; bx++)
            {
                var x0 = bx * 8;
                var y0 = by * 8;
                for (var y = 0; y < 8; y++)
                    for (var x = 0; x < 8; x++)
                        block[y * 8 + x] = plane[(y0 + y) * width + x0 + x] - 128;

                for (var row = 0; row < 8; row++) Dct(block, row * 8, 1);
                for (var col = 0; col < 8; col++) Dct(block, col, 8);

                var offset = (by * blocksX + bx) * 64;
                for (var k = 0; k < 64; k++)
                {
                    var n = JpegTables.Zigzag[k];
                    var v = block[n] * inverseQuant[n];
                    result[offset + k] = (short)(int)(v < 0 ? v - 0.5f : v + 0.5f);
                }
            }
        });
        return result;
    }

    private static void EmitBlock(short[] coefficients, int offset, ref int pred, HuffmanEncodeTable dc, HuffmanEncodeTable ac, JpegBitWriter bits)
    {
        var zigzag = coefficients.AsSpan(offset, 64);
        var diff = zigzag[0] - pred;
        pred = zigzag[0];
        var category = Category(diff);
        dc.Write(bits, category);
        if (category > 0) bits.Write((uint)(diff < 0 ? diff - 1 : diff), category);

        var run = 0;
        for (var k = 1; k < 64; k++)
        {
            var v = zigzag[k];
            if (v == 0) { run++; continue; }
            while (run > 15) { ac.Write(bits, 0xF0); run -= 16; }
            category = Category(v);
            ac.Write(bits, (run << 4) | category);
            bits.Write((uint)(v < 0 ? v - 1 : v), category);
            run = 0;
        }
        if (run > 0) ac.Write(bits, 0x00);
    }

    private static int Category(int v) => 32 - System.Numerics.BitOperations.LeadingZeroCount((uint)Math.Abs(v));

    /// <summary>One 8-point forward DCT (the Arai–Agui–Nakajima flowgraph) over <paramref name="data"/> at <paramref name="start"/>, element spacing <paramref name="stride"/>.</summary>
    private static void Dct(float[] data, int start, int stride)
    {
        var d0 = data[start];
        var d1 = data[start + stride];
        var d2 = data[start + stride * 2];
        var d3 = data[start + stride * 3];
        var d4 = data[start + stride * 4];
        var d5 = data[start + stride * 5];
        var d6 = data[start + stride * 6];
        var d7 = data[start + stride * 7];

        var tmp0 = d0 + d7; var tmp7 = d0 - d7;
        var tmp1 = d1 + d6; var tmp6 = d1 - d6;
        var tmp2 = d2 + d5; var tmp5 = d2 - d5;
        var tmp3 = d3 + d4; var tmp4 = d3 - d4;

        var tmp10 = tmp0 + tmp3;
        var tmp13 = tmp0 - tmp3;
        var tmp11 = tmp1 + tmp2;
        var tmp12 = tmp1 - tmp2;

        data[start] = tmp10 + tmp11;
        data[start + stride * 4] = tmp10 - tmp11;

        var z1 = (tmp12 + tmp13) * 0.707106781f;
        data[start + stride * 2] = tmp13 + z1;
        data[start + stride * 6] = tmp13 - z1;

        tmp10 = tmp4 + tmp5;
        tmp11 = tmp5 + tmp6;
        tmp12 = tmp6 + tmp7;

        var z5 = (tmp10 - tmp12) * 0.382683433f;
        var z2 = tmp10 * 0.541196100f + z5;
        var z4 = tmp12 * 1.306562965f + z5;
        var z3 = tmp11 * 0.707106781f;

        var z11 = tmp7 + z3;
        var z13 = tmp7 - z3;

        data[start + stride * 5] = z13 + z2;
        data[start + stride * 3] = z13 - z2;
        data[start + stride] = z11 + z4;
        data[start + stride * 7] = z11 - z4;
    }

    private sealed class HuffmanEncodeTable
    {
        private readonly ushort[] _code = new ushort[256];
        private readonly byte[] _size = new byte[256];

        public HuffmanEncodeTable(byte[] counts, byte[] values)
        {
            var code = 0;
            var k = 0;
            for (var length = 1; length <= 16; length++)
            {
                for (var i = 0; i < counts[length - 1]; i++, k++)
                {
                    _code[values[k]] = (ushort)code;
                    _size[values[k]] = (byte)length;
                    code++;
                }
                code <<= 1;
            }
        }

        public void Write(JpegBitWriter bits, int symbol) => bits.Write(_code[symbol], _size[symbol]);
    }
}
