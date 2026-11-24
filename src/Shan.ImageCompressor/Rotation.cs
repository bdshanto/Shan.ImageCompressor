namespace Shan.ImageCompressor;

/// <summary>A clockwise turn in quarter steps, applied after the image has been put upright from its EXIF orientation.</summary>
public enum Rotation
{
    /// <summary>Leave the image as it is.</summary>
    None = 0,

    /// <summary>A quarter turn clockwise.</summary>
    Rotate90 = 90,

    /// <summary>Upside down.</summary>
    Rotate180 = 180,

    /// <summary>A quarter turn anticlockwise.</summary>
    Rotate270 = 270
}
