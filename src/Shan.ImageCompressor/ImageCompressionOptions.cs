namespace Shan.ImageCompressor;

/// <summary>Tuning for <see cref="ImageCompressor.CompressAsync"/>. The defaults suit web-bound uploads.</summary>
public sealed class ImageCompressionOptions
{
    /// <summary>An image at or below this size is returned untouched. Default 768 KB.</summary>
    public long TargetSizeBytes { get; set; } = 768 * 1024;

    /// <summary>JPEG quality (1–100) to start from. Default 90.</summary>
    public int InitialQuality { get; set; } = 90;

    /// <summary>JPEG quality is stepped down by 10 until it reaches this floor, then dimensions shrink. Default 40.</summary>
    public int MinQuality { get; set; } = 40;

    /// <summary>The width never shrinks below this. Default 400.</summary>
    public int MinWidth { get; set; } = 400;

    /// <summary>Each shrink step multiplies both dimensions by this factor. Default 0.8.</summary>
    public double ShrinkFactor { get; set; } = 0.8;

    /// <summary>Hard cap on encode attempts, so a pathological image cannot loop for long. Default 25.</summary>
    public int MaxPasses { get; set; } = 25;

    /// <summary>
    /// The most pixels (width × height) an image may have to be decoded; a bigger one is rejected before any pixel memory is
    /// allocated. A decoded image takes 4 bytes per pixel, so this bounds memory against decompression bombs. Default 50 million.
    /// </summary>
    public long MaxPixels { get; set; } = 50_000_000;
}
