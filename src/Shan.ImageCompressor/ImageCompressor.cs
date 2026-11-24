using Shan.ImageCompressor.Bmp;
using Shan.ImageCompressor.Gif;
using Shan.ImageCompressor.Jpeg;
using Shan.ImageCompressor.Png;
using Shan.ImageCompressor.Processing;

namespace Shan.ImageCompressor;

/// <summary>Detects and compresses JPEG, PNG, GIF and BMP images using only this library's own codecs — no third-party imaging dependency.</summary>
public static class ImageCompressor
{
    /// <summary>
    /// The image format of the bytes themselves (not what a client claimed), or <see cref="ImageFormat.Unknown"/>.
    /// The stream must be seekable; its position is reset to 0 afterwards.
    /// </summary>
    public static async Task<ImageFormat> DetectFormatAsync(Stream content, CancellationToken ct = default)
    {
        content.Position = 0;
        try
        {
            var header = new byte[16];
            var read = 0;
            while (read < header.Length)
            {
                var n = await content.ReadAsync(header.AsMemory(read), ct);
                if (n == 0) break;
                read += n;
            }
            return ImageFormatDetector.Detect(header.AsSpan(0, read));
        }
        finally { content.Position = 0; }
    }

    /// <summary>The image format of the bytes themselves, or <see cref="ImageFormat.Unknown"/>.</summary>
    public static ImageFormat DetectFormat(ReadOnlySpan<byte> content) => ImageFormatDetector.Detect(content);

    /// <summary>
    /// Compresses an image only when it is bigger than <see cref="ImageCompressionOptions.TargetSizeBytes"/>; a small image is returned as-is.
    /// Opaque images become JPEG (quality stepped down, then dimensions); images with real transparency stay PNG (dimensions only),
    /// since JPEG would drop the alpha channel. EXIF orientation is applied and all metadata (EXIF/GPS/XMP/IPTC) is dropped.
    /// The input stream is read from its current position (position 0 when seekable) and not disposed.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="content"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An option has an unusable value (see <see cref="ImageCompressionOptions"/>).</exception>
    /// <exception cref="InvalidDataException">The stream is not an image, or is corrupt or over <see cref="ImageCompressionOptions.MaxPixels"/>.</exception>
    /// <exception cref="NotSupportedException">The image needs compressing but is not a JPEG (baseline or progressive), PNG, single-frame GIF or BMP (e.g. WebP, animated GIF, CMYK or arithmetic-coded JPEG).</exception>
    public static async Task<CompressedImage> CompressAsync(Stream content, ImageCompressionOptions? options = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        options ??= new ImageCompressionOptions();
        options.Validate();
        if (content.CanSeek) content.Position = 0;

        var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct);
        return CompressCore(buffer.ToArray(), options, ct);
    }

    /// <summary>Synchronous version of <see cref="CompressAsync"/>, for callers that are not async. Same behaviour and exceptions.</summary>
    public static CompressedImage Compress(Stream content, ImageCompressionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        options ??= new ImageCompressionOptions();
        options.Validate();
        if (content.CanSeek) content.Position = 0;

        var buffer = new MemoryStream();
        content.CopyTo(buffer);
        return CompressCore(buffer.ToArray(), options, CancellationToken.None);
    }

    /// <summary>Compresses an image held in memory. Same behaviour and exceptions as <see cref="CompressAsync"/>.</summary>
    public static CompressedImage Compress(byte[] content, ImageCompressionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        options ??= new ImageCompressionOptions();
        options.Validate();
        return CompressCore(content, options, CancellationToken.None);
    }

    private static CompressedImage CompressCore(byte[] data, ImageCompressionOptions options, CancellationToken ct)
    {
        var format = ImageFormatDetector.Detect(data);
        if (format == ImageFormat.Unknown) throw new InvalidDataException("The stream is not a recognised image.");

        if (data.Length <= options.TargetSizeBytes)
            return new CompressedImage(new MemoryStream(data), ImageFormatDetector.ContentType(format), ImageFormatDetector.Extension(format), false);

        RgbaImage original;
        switch (format)
        {
            case ImageFormat.Png:
                original = PngDecoder.Decode(data, options.MaxPixels);
                break;
            case ImageFormat.Jpeg:
                original = JpegDecoder.Decode(data, options.MaxPixels, out var orientation);
                original = Orienter.Apply(original, orientation);
                break;
            case ImageFormat.Gif:
                original = GifDecoder.Decode(data, options.MaxPixels);
                break;
            case ImageFormat.Bmp:
                original = BmpDecoder.Decode(data, options.MaxPixels);
                break;
            default:
                throw new NotSupportedException($"A {format} image can be detected but not compressed; only JPEG, PNG, GIF and BMP are supported.");
        }
        data = [];

        var keepAlpha = original.HasTransparency();
        var quality = options.InitialQuality;
        var width = original.Width;
        var height = original.Height;
        if (!keepAlpha && Math.Max(width, height) > JpegEncoder.MaxDimension)
        {
            var fit = (double)JpegEncoder.MaxDimension / Math.Max(width, height);
            width = Math.Max(1, (int)(width * fit));
            height = Math.Max(1, (int)(height * fit));
        }

        for (var pass = 1; ; pass++)
        {
            ct.ThrowIfCancellationRequested();
            var current = Resizer.Shrink(original, width, height);
            var output = keepAlpha ? PngEncoder.Encode(current) : JpegEncoder.Encode(current, quality);

            var smallest = keepAlpha ? width <= options.MinWidth : quality <= options.MinQuality && width <= options.MinWidth;
            if (output.Length <= options.TargetSizeBytes || smallest || pass >= options.MaxPasses)
            {
                return keepAlpha
                    ? new CompressedImage(output, "image/png", ".png", true)
                    : new CompressedImage(output, "image/jpeg", ".jpg", true);
            }

            output.Dispose();
            if (!keepAlpha && quality > options.MinQuality) quality = Math.Max(options.MinQuality, quality - 10);
            else
            {
                width = Math.Max(1, (int)(width * options.ShrinkFactor));
                height = Math.Max(1, (int)(height * options.ShrinkFactor));
            }
        }
    }
}
