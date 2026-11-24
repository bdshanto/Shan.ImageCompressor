namespace Shan.ImageCompressor;

/// <summary>The parts of an image's metadata the library can carry to its output when asked to (<see cref="ImageCompressionOptions.KeepMetadata"/>).</summary>
/// <param name="Exif">The raw EXIF TIFF block (starting at the "II"/"MM" byte-order mark, without the JPEG "Exif\0\0" prefix), or null.</param>
/// <param name="IccProfile">The embedded ICC colour profile, or null.</param>
internal sealed record ImageMetadata(byte[]? Exif, byte[]? IccProfile);
