namespace Shan.ImageCompressor;

/// <summary>A decoded image: 8-bit RGBA, row-major, 4 bytes per pixel, straight (non-premultiplied) alpha.</summary>
internal sealed class RgbaImage(int width, int height, byte[] pixels)
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public byte[] Pixels { get; } = pixels;

    public static RgbaImage Create(int width, int height) => new(width, height, new byte[checked(width * height * 4)]);

    /// <summary>Whether any pixel is actually see-through — decided from the pixels, not a declared colour type.</summary>
    public bool HasTransparency()
    {
        for (var i = 3; i < Pixels.Length; i += 4)
            if (Pixels[i] != 255) return true;
        return false;
    }
}
