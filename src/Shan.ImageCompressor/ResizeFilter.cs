namespace Shan.ImageCompressor;

/// <summary>How pixels are blended when an image is made smaller.</summary>
public enum ResizeFilter
{
    /// <summary>Exact area averaging. Fast, never rings or overshoots, and the default.</summary>
    Box = 0,

    /// <summary>Bilinear (tent). A little smoother than <see cref="Box"/> at modest reductions.</summary>
    Triangle,

    /// <summary>Bicubic (Catmull-Rom). Noticeably sharper, with a faint halo on hard edges.</summary>
    CatmullRom,

    /// <summary>Lanczos with three lobes. The sharpest, and the slowest; best for photographs reduced a lot.</summary>
    Lanczos3
}
