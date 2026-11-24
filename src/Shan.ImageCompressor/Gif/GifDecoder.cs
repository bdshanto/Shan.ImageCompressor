using System.Buffers.Binary;

namespace Shan.ImageCompressor.Gif;

/// <summary>
/// Decodes a GIF to 8-bit RGBA on a transparent logical screen: global/local palettes, interlacing, and the transparent colour index.
/// <see cref="Decode"/> reads a still and rejects an animation with <see cref="NotSupportedException"/>; <see cref="DecodeAnimation"/> reads
/// every frame, composited with its disposal method, delay and the loop count.
/// </summary>
internal static class GifDecoder
{
    public static RgbaImage Decode(byte[] data, long maxPixels)
    {
        try { return DecodeCore(data, maxPixels); }
        catch (Exception e) when (e is IndexOutOfRangeException or ArgumentException or OverflowException)
        {
            throw new InvalidDataException("The GIF data is corrupt.", e);
        }
    }

    /// <summary>Checks the signature and logical screen, and returns the screen size, the global palette (if any) and where the blocks start.</summary>
    private static (int Width, int Height, byte[]? Palette, int Pos) ReadHeader(byte[] data, long maxPixels)
    {
        if (data.Length < 13 || !(data.AsSpan(0, 6).SequenceEqual("GIF87a"u8) || data.AsSpan(0, 6).SequenceEqual("GIF89a"u8))) throw new InvalidDataException("Not a GIF.");

        var screenWidth = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(6));
        var screenHeight = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(8));
        var flags = data[10];
        if (screenWidth == 0 || screenHeight == 0) throw new InvalidDataException("The GIF has invalid dimensions.");
        if ((long)screenWidth * screenHeight > maxPixels) throw new InvalidDataException($"The image has {(long)screenWidth * screenHeight} pixels, over the {maxPixels} limit.");

        var pos = 13;
        byte[]? globalPalette = null;
        if ((flags & 0x80) != 0)
        {
            var size = 3 * (1 << ((flags & 7) + 1));
            if (pos + size > data.Length) throw new InvalidDataException("The GIF palette is truncated.");
            globalPalette = data.AsSpan(pos, size).ToArray();
            pos += size;
        }
        return (screenWidth, screenHeight, globalPalette, pos);
    }

    /// <summary>Decodes every frame, each as the full logical screen as a viewer would show it (disposal methods and transparency applied).</summary>
    public static GifAnimation DecodeAnimation(byte[] data, long maxPixels)
    {
        try { return DecodeAnimationCore(data, maxPixels); }
        catch (Exception e) when (e is IndexOutOfRangeException or ArgumentException or OverflowException)
        {
            throw new InvalidDataException("The GIF data is corrupt.", e);
        }
    }

    private static GifAnimation DecodeAnimationCore(byte[] data, long maxPixels)
    {
        var (screenWidth, screenHeight, globalPalette, pos) = ReadHeader(data, maxPixels);

        var canvas = RgbaImage.Create(screenWidth, screenHeight);
        var frames = new List<GifFrame>();
        var loop = -1; // no NETSCAPE extension: play once
        int transparentIndex = -1, delay = 0, disposal = 0;
        FrameData? previous = null;
        var previousDisposal = 0;
        byte[]? beforePrevious = null; // the screen as it was before the previous frame was drawn, for disposal 3

        while (pos < data.Length)
        {
            var block = data[pos++];
            if (block == 0x3B) break;

            if (block == 0x21)
            {
                if (pos >= data.Length) throw new InvalidDataException("The GIF data is truncated.");
                var label = data[pos++];
                if (label == 0xF9 && pos + 5 < data.Length && data[pos] >= 4) // graphic control extension: applies to the next frame only
                {
                    var control = data[pos + 1];
                    disposal = (control >> 2) & 7;
                    transparentIndex = (control & 1) != 0 ? data[pos + 4] : -1;
                    delay = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos + 2));
                }
                else if (label == 0xFF && pos + 16 <= data.Length && data[pos] == 11 && data.AsSpan(pos + 1, 11).SequenceEqual("NETSCAPE2.0"u8)
                         && data[pos + 12] >= 3 && data[pos + 13] == 1)
                    loop = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos + 14));
                pos = SkipSubBlocks(data, pos);
                continue;
            }

            if (block != 0x2C) throw new InvalidDataException("The GIF contains an unknown block.");

            var frame = ReadFrame(data, ref pos, screenWidth, screenHeight, globalPalette);

            // the previous frame's disposal happens before this one is drawn
            if (previous != null)
            {
                if (previousDisposal == 2) Clear(canvas, previous, screenWidth);
                else if (previousDisposal == 3 && beforePrevious != null) beforePrevious.CopyTo(canvas.Pixels, 0);
            }
            beforePrevious = disposal == 3 ? (byte[])canvas.Pixels.Clone() : null;

            if ((long)(frames.Count + 1) * screenWidth * screenHeight > maxPixels)
                throw new InvalidDataException($"The animation has more than {maxPixels} pixels across its frames; raise MaxPixels to allow it.");

            Paint(canvas, frame, screenWidth, transparentIndex);
            frames.Add(new GifFrame(new RgbaImage(screenWidth, screenHeight, (byte[])canvas.Pixels.Clone()), delay));

            previous = frame;
            previousDisposal = disposal;
            transparentIndex = -1; delay = 0; disposal = 0;
        }

        if (frames.Count == 0) throw new InvalidDataException("The GIF has no image data.");
        return new GifAnimation(screenWidth, screenHeight, frames, loop);
    }

    private static void Clear(RgbaImage canvas, FrameData frame, int screenWidth)
    {
        for (var y = 0; y < frame.Height; y++)
            Array.Clear(canvas.Pixels, ((frame.Top + y) * screenWidth + frame.Left) * 4, frame.Width * 4);
    }

    private static RgbaImage DecodeCore(byte[] data, long maxPixels)
    {
        var (screenWidth, screenHeight, globalPalette, pos) = ReadHeader(data, maxPixels);

        var transparentIndex = -1;
        RgbaImage? image = null;

        while (pos < data.Length)
        {
            var block = data[pos++];
            if (block == 0x3B) break; // trailer

            if (block == 0x21) // extension
            {
                if (pos >= data.Length) throw new InvalidDataException("The GIF data is truncated.");
                var label = data[pos++];
                if (label == 0xF9 && pos + 5 < data.Length && data[pos] >= 4) // graphic control extension
                    transparentIndex = (data[pos + 1] & 1) != 0 ? data[pos + 4] : -1;
                pos = SkipSubBlocks(data, pos);
                continue;
            }

            if (block != 0x2C) throw new InvalidDataException("The GIF contains an unknown block.");

            if (image != null) throw new NotSupportedException("Animated GIFs are not supported; only single-frame GIFs can be compressed.");
            image = DecodeFrame(data, ref pos, screenWidth, screenHeight, globalPalette, transparentIndex);
        }

        return image ?? throw new InvalidDataException("The GIF has no image data.");
    }

    /// <summary>One frame as stored: its rectangle on the screen, its palette indices (already in row order) and the palette they refer to.</summary>
    private sealed record FrameData(int Left, int Top, int Width, int Height, byte[] Indices, byte[] Palette, int[]? RowOrder);

    private static RgbaImage DecodeFrame(byte[] data, ref int pos, int screenWidth, int screenHeight, byte[]? globalPalette, int transparentIndex)
    {
        var frame = ReadFrame(data, ref pos, screenWidth, screenHeight, globalPalette);
        var image = RgbaImage.Create(screenWidth, screenHeight); // everything outside the frame stays transparent
        Paint(image, frame, screenWidth, transparentIndex);

        // Anything left in the stream must not be another frame, or this is an animation.
        while (pos < data.Length)
        {
            var next = data[pos++];
            if (next == 0x3B) break;
            if (next == 0x2C) throw new NotSupportedException("Animated GIFs are not supported; only single-frame GIFs can be compressed.");
            if (next != 0x21) throw new InvalidDataException("The GIF contains an unknown block.");
            pos++; // extension label
            pos = SkipSubBlocks(data, pos);
        }
        return image;
    }

    private static FrameData ReadFrame(byte[] data, ref int pos, int screenWidth, int screenHeight, byte[]? globalPalette)
    {
        if (pos + 9 > data.Length) throw new InvalidDataException("The GIF data is truncated.");
        int left = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos));
        int top = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos + 2));
        int width = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos + 4));
        int height = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos + 6));
        var flags = data[pos + 8];
        pos += 9;

        var palette = globalPalette;
        if ((flags & 0x80) != 0)
        {
            var size = 3 * (1 << ((flags & 7) + 1));
            if (pos + size > data.Length) throw new InvalidDataException("The GIF palette is truncated.");
            palette = data.AsSpan(pos, size).ToArray();
            pos += size;
        }
        if (palette == null) throw new InvalidDataException("The GIF has no colour palette.");
        if (width == 0 || height == 0) throw new InvalidDataException("The GIF frame has invalid dimensions.");
        if (left + width > screenWidth || top + height > screenHeight) throw new InvalidDataException("The GIF frame lies outside the logical screen.");

        if (pos >= data.Length) throw new InvalidDataException("The GIF data is truncated.");
        var minCodeSize = data[pos++];
        if (minCodeSize is < 2 or > 8) throw new InvalidDataException("The GIF has an invalid LZW code size.");

        var compressed = new MemoryStream();
        while (true)
        {
            if (pos >= data.Length) throw new InvalidDataException("The GIF data is truncated.");
            var length = data[pos++];
            if (length == 0) break;
            if (pos + length > data.Length) throw new InvalidDataException("The GIF data is truncated.");
            compressed.Write(data, pos, length);
            pos += length;
        }

        var indices = Lzw(compressed.GetBuffer().AsSpan(0, (int)compressed.Length), minCodeSize, width * height);
        return new FrameData(left, top, width, height, indices, palette, (flags & 0x40) != 0 ? InterlacedRows(height) : null);
    }

    /// <summary>Draws a frame onto the screen; pixels with the transparent index leave whatever is already there.</summary>
    private static void Paint(RgbaImage image, FrameData frame, int screenWidth, int transparentIndex)
    {
        var palette = frame.Palette;
        for (var i = 0; i < frame.Width * frame.Height; i++)
        {
            var index = frame.Indices[i];
            if (index == transparentIndex) continue;
            var y = frame.Top + (frame.RowOrder != null ? frame.RowOrder[i / frame.Width] : i / frame.Width);
            var o = (y * screenWidth + frame.Left + i % frame.Width) * 4;
            var p = index * 3;
            if (p + 2 >= palette.Length)
            {
                image.Pixels[o] = image.Pixels[o + 1] = image.Pixels[o + 2] = 0; // an index past the palette is opaque black
                image.Pixels[o + 3] = 255;
                continue;
            }
            image.Pixels[o] = palette[p];
            image.Pixels[o + 1] = palette[p + 1];
            image.Pixels[o + 2] = palette[p + 2];
            image.Pixels[o + 3] = 255;
        }
    }

    /// <summary>For frame row r in the stored (interlaced) order, the actual row it belongs to.</summary>
    private static int[] InterlacedRows(int height)
    {
        var rows = new int[height];
        var n = 0;
        foreach (var (start, step) in new[] { (0, 8), (4, 8), (2, 4), (1, 2) })
            for (var y = start; y < height; y += step) rows[n++] = y;
        return rows;
    }

    private static int SkipSubBlocks(byte[] data, int pos)
    {
        while (true)
        {
            if (pos >= data.Length) throw new InvalidDataException("The GIF data is truncated.");
            var length = data[pos++];
            if (length == 0) return pos;
            pos += length;
        }
    }

    /// <summary>GIF's variable-width LZW. A stream that ends early leaves the remaining pixels as index 0.</summary>
    private static byte[] Lzw(ReadOnlySpan<byte> input, int minCodeSize, int pixelCount)
    {
        var output = new byte[pixelCount];
        var clear = 1 << minCodeSize;
        var end = clear + 1;

        var prefix = new short[4096];
        var suffix = new byte[4096];
        var stack = new byte[4097];
        for (var i = 0; i < clear; i++) suffix[i] = (byte)i;

        var codeSize = minCodeSize + 1;
        var next = end + 1;
        var previous = -1;
        var first = (byte)0;
        var bits = 0;
        var bitCount = 0;
        var written = 0;
        var inputPos = 0;

        while (written < pixelCount)
        {
            while (bitCount < codeSize)
            {
                if (inputPos >= input.Length) return output;
                bits |= input[inputPos++] << bitCount;
                bitCount += 8;
            }
            var code = bits & ((1 << codeSize) - 1);
            bits >>= codeSize;
            bitCount -= codeSize;

            if (code == clear)
            {
                codeSize = minCodeSize + 1;
                next = end + 1;
                previous = -1;
                continue;
            }
            if (code == end) return output;

            if (previous == -1)
            {
                if (code >= clear) throw new InvalidDataException("The GIF LZW data is corrupt.");
                output[written++] = first = suffix[code];
                previous = code;
                continue;
            }

            var current = code;
            var top = 0;
            if (code >= next) // the code being defined right now (KwKwK case)
            {
                if (code > next) throw new InvalidDataException("The GIF LZW data is corrupt.");
                stack[top++] = first;
                code = previous;
            }
            while (code >= clear)
            {
                if (top >= stack.Length - 1) throw new InvalidDataException("The GIF LZW data is corrupt.");
                stack[top++] = suffix[code];
                code = prefix[code];
            }
            first = suffix[code];
            stack[top++] = first;

            if (next < 4096)
            {
                prefix[next] = (short)previous;
                suffix[next] = first;
                next++;
                if (next == 1 << codeSize && codeSize < 12) codeSize++;
            }
            previous = current;

            while (top > 0 && written < pixelCount) output[written++] = stack[--top];
        }
        return output;
    }
}
