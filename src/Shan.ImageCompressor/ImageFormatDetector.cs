namespace Shan.ImageCompressor;

internal static class ImageFormatDetector
{
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];

    public static ImageFormat Detect(ReadOnlySpan<byte> d) => Detect(d, d.Length);

    /// <summary><paramref name="totalLength"/> is the size of the whole file; <paramref name="d"/> may be just its first bytes.</summary>
    public static ImageFormat Detect(ReadOnlySpan<byte> d, long totalLength)
    {
        if (d.Length >= 8 && d[..8].SequenceEqual(PngSignature)) return ImageFormat.Png;
        if (d.Length >= 3 && d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF) return ImageFormat.Jpeg;
        if (d.Length >= 6 && (d[..6].SequenceEqual("GIF87a"u8) || d[..6].SequenceEqual("GIF89a"u8))) return ImageFormat.Gif;
        if (d.Length >= 12 && d[..4].SequenceEqual("RIFF"u8) && d[8..12].SequenceEqual("WEBP"u8)) return ImageFormat.WebP;
        if (d.Length >= 14 && d[0] == (byte)'B' && d[1] == (byte)'M') return ImageFormat.Bmp;
        if (d.Length >= 8 && (d[..4].SequenceEqual("II*\0"u8) || d[..4].SequenceEqual("MM\0*"u8) || d[..4].SequenceEqual("II+\0"u8) || d[..4].SequenceEqual("MM\0+"u8))) return ImageFormat.Tiff;
        if (d.Length >= 4 && d[..4].SequenceEqual("qoif"u8)) return ImageFormat.Qoi;
        if (d.Length >= 3 && d[0] == (byte)'P' && d[1] is >= (byte)'1' and <= (byte)'6' && d[2] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') return ImageFormat.Pnm;
        if (Tga.TgaDecoder.LooksLikeTga(d, totalLength)) return ImageFormat.Tga;
        return ImageFormat.Unknown;
    }

    public static string ContentType(ImageFormat format) => format switch
    {
        ImageFormat.Jpeg => "image/jpeg",
        ImageFormat.Png => "image/png",
        ImageFormat.Gif => "image/gif",
        ImageFormat.Bmp => "image/bmp",
        ImageFormat.WebP => "image/webp",
        ImageFormat.Tiff => "image/tiff",
        ImageFormat.Tga => "image/x-tga",
        ImageFormat.Pnm => "image/x-portable-anymap",
        ImageFormat.Qoi => "image/qoi",
        _ => "application/octet-stream"
    };

    public static string Extension(ImageFormat format) => format switch
    {
        ImageFormat.Jpeg => ".jpg",
        ImageFormat.Png => ".png",
        ImageFormat.Gif => ".gif",
        ImageFormat.Bmp => ".bmp",
        ImageFormat.WebP => ".webp",
        ImageFormat.Tiff => ".tif",
        ImageFormat.Tga => ".tga",
        ImageFormat.Pnm => ".pnm",
        ImageFormat.Qoi => ".qoi",
        _ => ".img"
    };
}
