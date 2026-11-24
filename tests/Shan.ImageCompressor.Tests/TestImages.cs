using Shan.ImageCompressor.Png;

namespace Shan.ImageCompressor.Tests;

internal static class TestImages
{
    /// <summary>A deterministic image whose pixels don't compress well (so size-driven passes really have work to do).</summary>
    public static RgbaImage Noisy(int width, int height, bool transparent = false)
    {
        var random = new Random(1);
        var image = RgbaImage.Create(width, height);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var o = (y * width + x) * 4;
                image.Pixels[o] = (byte)random.Next(256);
                image.Pixels[o + 1] = (byte)random.Next(256);
                image.Pixels[o + 2] = (byte)random.Next(256);
                image.Pixels[o + 3] = transparent && x % 2 == 0 ? (byte)0 : (byte)255;
            }
        }
        return image;
    }

    public static MemoryStream NoisyPng(int size, bool transparent) => PngEncoder.Encode(Noisy(size, size, transparent));

    public static double MeanAbsoluteDifference(byte[] a, byte[] b, bool ignoreAlpha = false)
    {
        long sum = 0;
        var count = 0;
        for (var i = 0; i < a.Length; i++)
        {
            if (ignoreAlpha && i % 4 == 3) continue;
            sum += Math.Abs(a[i] - b[i]);
            count++;
        }
        return (double)sum / count;
    }
}
