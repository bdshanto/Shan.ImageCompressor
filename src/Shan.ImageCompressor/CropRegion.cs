namespace Shan.ImageCompressor;

/// <summary>A rectangle of the upright image to keep, in pixels from the top-left corner. Parts outside the image are ignored.</summary>
public readonly record struct CropRegion(int X, int Y, int Width, int Height);
