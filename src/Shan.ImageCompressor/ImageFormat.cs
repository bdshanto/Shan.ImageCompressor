namespace Shan.ImageCompressor;

/// <summary>An image container format recognised from the first bytes of a file.</summary>
public enum ImageFormat
{
    /// <summary>Not an image this library recognises.</summary>
    Unknown = 0,

    /// <summary>JPEG. Detected and compressible (baseline and progressive; not CMYK or arithmetic-coded).</summary>
    Jpeg,

    /// <summary>PNG. Detected and compressible.</summary>
    Png,

    /// <summary>GIF. Detected only — returned as-is when small, rejected when it would need compressing.</summary>
    Gif,

    /// <summary>BMP. Detected only — returned as-is when small, rejected when it would need compressing.</summary>
    Bmp,

    /// <summary>WebP. Detected only — returned as-is when small, rejected when it would need compressing.</summary>
    WebP
}
