namespace Shan.ImageCompressor;

/// <summary>A decoded image: 8-bit RGBA, row-major, 4 bytes per pixel, straight (non-premultiplied) alpha.</summary>
internal sealed class RgbaImage(int width, int height, byte[] pixels)
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public byte[] Pixels { get; } = pixels;

    /// <summary>EXIF and ICC profile found in the source file, if any. Not part of the pixels; carried to the output only on request.</summary>
    public ImageMetadata? Metadata { get; set; }

    public static RgbaImage Create(int width, int height) => new(width, height, new byte[checked(width * height * 4)]);

    /// <summary>A copy with every pixel composited over white and made opaque (for output formats without alpha).</summary>
    public RgbaImage FlattenOnWhite()
    {
        var copy = new RgbaImage(Width, Height, (byte[])Pixels.Clone()) { Metadata = Metadata };
        var p = copy.Pixels;
        for (var i = 0; i < p.Length; i += 4)
        {
            var a = p[i + 3];
            if (a == 255) continue;
            for (var c = 0; c < 3; c++) p[i + c] = (byte)((p[i + c] * a + 255 * (255 - a) + 127) / 255);
            p[i + 3] = 255;
        }
        return copy;
    }

    /// <summary>Whether any pixel is actually see-through — decided from the pixels, not a declared colour type.</summary>
    public bool HasTransparency()
    {
        for (var i = 3; i < Pixels.Length; i += 4)
            if (Pixels[i] != 255) return true;
        return false;
    }
}
