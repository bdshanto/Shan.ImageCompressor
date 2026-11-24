namespace Shan.ImageCompressor;

/// <summary>Tuning for <see cref="ImageCompressor.CompressAsync"/> and the synchronous <c>Compress</c> overloads. The defaults suit web-bound uploads.</summary>
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

    /// <summary>Which format a compressed image is written in. Default <see cref="OutputFormat.Auto"/>.</summary>
    public OutputFormat Output { get; set; } = OutputFormat.Auto;

    /// <summary>
    /// How many cores one call may use for resizing, colour conversion and JPEG encoding. Default 0 = all of them. Use 1 on a busy server
    /// that already handles many requests at once, so each call stays on one core. The output is identical whatever the setting.
    /// </summary>
    public int MaxDegreeOfParallelism { get; set; }

    /// <summary>
    /// Carry the source's EXIF (camera, date, GPS, ...) and — when it is not converted to sRGB — its ICC colour profile to the output.
    /// Default false: metadata is dropped, which is what you want for user uploads (it removes GPS locations). The orientation tag is
    /// always reset, because the pixels are rotated upright. Has no effect on an image that is already small enough and returned as-is.
    /// </summary>
    public bool KeepMetadata { get; set; }

    /// <summary>
    /// Convert pixels from an embedded RGB matrix/TRC ICC profile (Adobe RGB, Display P3, ProPhoto, camera profiles) to sRGB, so the
    /// colours stay right once the profile is dropped. Default true. Lookup-table (e.g. CMYK) profiles are not converted.
    /// </summary>
    public bool ConvertToSrgb { get; set; } = true;

    /// <summary>Rejects values that could never produce a sensible result (a shrink factor of 1 or more would never shrink, etc.).</summary>
    internal void Validate()
    {
        if (TargetSizeBytes < 1) throw new ArgumentOutOfRangeException(nameof(TargetSizeBytes), TargetSizeBytes, "Must be at least 1.");
        if (InitialQuality is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(InitialQuality), InitialQuality, "Must be between 1 and 100.");
        if (MinQuality is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(MinQuality), MinQuality, "Must be between 1 and 100.");
        if (MinWidth < 1) throw new ArgumentOutOfRangeException(nameof(MinWidth), MinWidth, "Must be at least 1.");
        if (!(ShrinkFactor > 0 && ShrinkFactor < 1)) throw new ArgumentOutOfRangeException(nameof(ShrinkFactor), ShrinkFactor, "Must be greater than 0 and less than 1.");
        if (MaxPasses < 1) throw new ArgumentOutOfRangeException(nameof(MaxPasses), MaxPasses, "Must be at least 1.");
        if (MaxDegreeOfParallelism < 0) throw new ArgumentOutOfRangeException(nameof(MaxDegreeOfParallelism), MaxDegreeOfParallelism, "Must be 0 (all cores) or more.");
        if (!Enum.IsDefined(Output)) throw new ArgumentOutOfRangeException(nameof(Output), Output, "Not a known output format.");
        if (MaxPixels < 1) throw new ArgumentOutOfRangeException(nameof(MaxPixels), MaxPixels, "Must be at least 1.");
    }
}
