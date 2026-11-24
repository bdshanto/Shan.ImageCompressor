using System.Buffers.Binary;
using Shan.ImageCompressor.Jpeg;

namespace Shan.ImageCompressor.WebP;

/// <summary>
/// Reads a still WebP: the RIFF container (plain lossy "VP8 ", plain lossless "VP8L", or the extended "VP8X" layout with an alpha plane,
/// ICC profile and EXIF) and hands the bitstream to the lossy or lossless decoder. Animated WebP is rejected.
/// </summary>
internal static class WebPDecoder
{
    private const int HasIcc = 0x20, HasAlpha = 0x10, HasExif = 0x08, IsAnimated = 0x02;

    public static RgbaImage Decode(byte[] data, long maxPixels, out int orientation)
    {
        try { return DecodeCore(data, maxPixels, out orientation); }
        catch (Exception e) when (e is IndexOutOfRangeException or ArgumentException or OverflowException or InvalidOperationException)
        {
            throw new InvalidDataException("The WebP data is corrupt.", e);
        }
    }

    private static RgbaImage DecodeCore(byte[] data, long maxPixels, out int orientation)
    {
        orientation = 1;
        if (data.Length < 20 || !data.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !data.AsSpan(8, 4).SequenceEqual("WEBP"u8)) throw new InvalidDataException("Not a WebP.");

        // the RIFF size says where the file really ends; trailing bytes beyond it are ignored, a shorter file is truncated
        var riffEnd = Math.Min(data.Length, 8L + BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4)));
        int vp8Start = -1, vp8Size = 0, alphStart = -1, alphSize = 0, flags = 0, canvasWidth = 0, canvasHeight = 0;
        var lossless = false;
        byte[]? icc = null, exif = null;

        var pos = 12;
        while (pos + 8 <= riffEnd)
        {
            var tag = data.AsSpan(pos, 4);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos + 4));
            var body = pos + 8;
            if (body + (long)size > riffEnd) throw new InvalidDataException("The WebP data is truncated.");

            if (tag.SequenceEqual("VP8X"u8))
            {
                if (size < 10) throw new InvalidDataException("The WebP header is invalid.");
                flags = data[body];
                canvasWidth = 1 + (data[body + 4] | data[body + 5] << 8 | data[body + 6] << 16);
                canvasHeight = 1 + (data[body + 7] | data[body + 8] << 8 | data[body + 9] << 16);
            }
            else if (tag.SequenceEqual("ICCP"u8)) icc = data.AsSpan(body, (int)size).ToArray();
            else if (tag.SequenceEqual("EXIF"u8))
            {
                var block = data.AsSpan(body, (int)size);
                if (block.Length > 6 && block[..6].SequenceEqual("Exif\0\0"u8)) block = block[6..]; // some writers keep the JPEG-style prefix
                exif = block.ToArray();
            }
            else if (tag.SequenceEqual("ALPH"u8) && alphStart < 0) { alphStart = body; alphSize = (int)size; }
            else if ((tag.SequenceEqual("VP8 "u8) || tag.SequenceEqual("VP8L"u8)) && vp8Start < 0)
            {
                vp8Start = body; vp8Size = (int)size;
                lossless = tag[3] == (byte)'L';
            }
            else if (tag.SequenceEqual("ANMF"u8) || tag.SequenceEqual("ANIM"u8))
                throw new NotSupportedException("Animated WebP is not supported; only still WebP images can be compressed.");

            pos = (int)Math.Min(riffEnd, body + (long)size + (size & 1)); // chunks are padded to an even length
        }

        if ((flags & IsAnimated) != 0) throw new NotSupportedException("Animated WebP is not supported; only still WebP images can be compressed.");
        if (vp8Start < 0) throw new InvalidDataException("The WebP has no image data.");

        var stream = data.AsSpan(vp8Start, vp8Size);
        RgbaImage image;
        if (lossless)
        {
            image = Vp8LDecoder.Decode(stream, maxPixels);
        }
        else
        {
            image = Vp8Decoder.Decode(stream, maxPixels);
            if (alphStart >= 0 && (flags & HasAlpha) != 0)
                AlphaPlane.Apply(data.AsSpan(alphStart, alphSize), image);
        }

        if (canvasWidth != 0 && (canvasWidth != image.Width || canvasHeight != image.Height))
            throw new InvalidDataException("The WebP canvas size does not match its image.");

        if (exif != null)
        {
            var prefixed = new byte[exif.Length + 6];
            "Exif\0\0"u8.CopyTo(prefixed);
            exif.CopyTo(prefixed, 6);
            orientation = ExifOrientation.Read(prefixed);
        }
        if (icc != null || exif != null) image.Metadata = new ImageMetadata(exif, icc);
        return image;
    }
}
