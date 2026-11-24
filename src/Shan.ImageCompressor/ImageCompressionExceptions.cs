namespace Shan.ImageCompressor;

/// <summary>Base class of every exception <see cref="ImageCompressor"/> throws about the image itself (not about bad arguments).</summary>
public abstract class ImageCompressionException : Exception
{
    /// <summary>The format the bytes were recognised as, or <see cref="ImageFormat.Unknown"/> when they were not recognised.</summary>
    public ImageFormat Format { get; }

    /// <summary>Creates the exception.</summary>
    protected ImageCompressionException(string message, ImageFormat format, Exception? innerException)
        : base(message, innerException) => Format = format;
}

/// <summary>The input is not an image, is corrupt or truncated, or exceeds <see cref="ImageCompressionOptions.MaxPixels"/>.</summary>
public sealed class InvalidImageException : ImageCompressionException
{
    /// <summary>Creates the exception.</summary>
    public InvalidImageException(string message, ImageFormat format = ImageFormat.Unknown, Exception? innerException = null)
        : base(message, format, innerException) { }
}

/// <summary>
/// The image is a recognised format but uses a feature this library cannot decode (WebP, an animated GIF, a CMYK-only or JPEG-compressed
/// TIFF, an arithmetic-coded JPEG, ...). Callers typically keep the original file when they see this.
/// </summary>
public sealed class UnsupportedImageException : ImageCompressionException
{
    /// <summary>Creates the exception.</summary>
    public UnsupportedImageException(string message, ImageFormat format = ImageFormat.Unknown, Exception? innerException = null)
        : base(message, format, innerException) { }
}
