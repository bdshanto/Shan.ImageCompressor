namespace Shan.ImageCompressor.WebP;

internal static class Vp8Decoder
{
    public static RgbaImage Decode(ReadOnlySpan<byte> data, long maxPixels) => throw new NotSupportedException("Lossy WebP is not implemented yet.");
}
