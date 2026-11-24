namespace Shan.ImageCompressor;

internal static class ImageFormatDetector
{
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];

    public static ImageFormat Detect(ReadOnlySpan<byte> d)
    {
        if (d.Length >= 8 && d[..8].SequenceEqual(PngSignature)) return ImageFormat.Png;
        if (d.Length >= 3 && d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF) return ImageFormat.Jpeg;
        if (d.Length >= 6 && (d[..6].SequenceEqual("GIF87a"u8) || d[..6].SequenceEqual("GIF89a"u8))) return ImageFormat.Gif;
        if (d.Length >= 12 && d[..4].SequenceEqual("RIFF"u8) && d[8..12].SequenceEqual("WEBP"u8)) return ImageFormat.WebP;
        if (d.Length >= 14 && d[0] == (byte)'B' && d[1] == (byte)'M') return ImageFormat.Bmp;
        return ImageFormat.Unknown;
    }

    public static string ContentType(ImageFormat format) => format switch
    {
        ImageFormat.Jpeg => "image/jpeg",
        ImageFormat.Png => "image/png",
        ImageFormat.Gif => "image/gif",
        ImageFormat.Bmp => "image/bmp",
        ImageFormat.WebP => "image/webp",
        _ => "application/octet-stream"
    };

    public static string Extension(ImageFormat format) => format switch
    {
        ImageFormat.Jpeg => ".jpg",
        ImageFormat.Png => ".png",
        ImageFormat.Gif => ".gif",
        ImageFormat.Bmp => ".bmp",
        ImageFormat.WebP => ".webp",
        _ => ".img"
    };
}
