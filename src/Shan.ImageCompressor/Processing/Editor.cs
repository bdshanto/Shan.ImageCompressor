namespace Shan.ImageCompressor.Processing;

/// <summary>The caller-requested edits: crop, quarter-turn rotation and mirroring.</summary>
internal static class Editor
{
    /// <summary>The part of <paramref name="source"/> inside <paramref name="region"/>; a region reaching past the edge is clipped to it.</summary>
    public static RgbaImage Crop(RgbaImage source, CropRegion region)
    {
        if (region.X >= source.Width || region.Y >= source.Height)
            throw new ArgumentOutOfRangeException(nameof(ImageCompressionOptions.Crop), region, $"The crop region starts outside the {source.Width}x{source.Height} image.");

        var width = Math.Min(region.Width, source.Width - region.X);
        var height = Math.Min(region.Height, source.Height - region.Y);
        if (width == source.Width && height == source.Height) return source;

        var result = RgbaImage.Create(width, height);
        for (var y = 0; y < height; y++)
            Buffer.BlockCopy(source.Pixels, ((region.Y + y) * source.Width + region.X) * 4, result.Pixels, y * width * 4, width * 4);
        result.Metadata = source.Metadata;
        return result;
    }

    /// <summary>Turns clockwise, then mirrors. Reuses the EXIF orientation transforms: 6 = 90° clockwise, 3 = 180°, 8 = 270°, 2 = horizontal flip, 4 = vertical flip.</summary>
    public static RgbaImage Transform(RgbaImage source, Rotation rotation, bool flipHorizontal, bool flipVertical)
    {
        var result = rotation switch
        {
            Rotation.Rotate90 => Orienter.Apply(source, 6),
            Rotation.Rotate180 => Orienter.Apply(source, 3),
            Rotation.Rotate270 => Orienter.Apply(source, 8),
            _ => source
        };
        if (flipHorizontal) result = Orienter.Apply(result, 2);
        if (flipVertical) result = Orienter.Apply(result, 4);
        return result;
    }
}
