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

    /// <summary>TIFF (classic, first image). Compressible when uncompressed, PackBits, LZW or Deflate; see the README for the supported colour models.</summary>
    Tiff,

    /// <summary>Truevision TGA. Detected by a strict header check, since the format has no magic number.</summary>
    Tga,

    /// <summary>Netpbm (PBM/PGM/PPM, ASCII or binary).</summary>
    Pnm,

    /// <summary>QOI ("Quite OK Image").</summary>
    Qoi,

    /// <summary>WebP. Detected only — returned as-is when small, rejected when it would need compressing.</summary>
    WebP
}
