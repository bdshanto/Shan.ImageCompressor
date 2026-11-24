namespace Shan.ImageCompressor.WebP;

internal static class AlphaPlane
{
    public static void Apply(ReadOnlySpan<byte> chunk, RgbaImage image) => throw new NotSupportedException("WebP alpha is not implemented yet.");
}
