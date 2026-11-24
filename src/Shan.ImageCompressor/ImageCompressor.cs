using Shan.ImageCompressor.Bmp;
using Shan.ImageCompressor.Color;
using Shan.ImageCompressor.Gif;
using Shan.ImageCompressor.Jpeg;
using Shan.ImageCompressor.Pnm;
using Shan.ImageCompressor.Png;
using Shan.ImageCompressor.Processing;
using Shan.ImageCompressor.Qoi;
using Shan.ImageCompressor.Tga;
using Shan.ImageCompressor.Tiff;
using Shan.ImageCompressor.WebP;

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
            var header = new byte[32];
            var read = 0;
            while (read < header.Length)
            {
                var n = await content.ReadAsync(header.AsMemory(read), ct);
                if (n == 0) break;
                read += n;
            }
            return ImageFormatDetector.Detect(header.AsSpan(0, read), content.Length);
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
        using var scope = Work.Use(options.MaxDegreeOfParallelism);
        try { return CompressCore(data, format, options, ct); }
        catch (InvalidDataException e) { throw new InvalidImageException(e.Message, format, e); }
        catch (NotSupportedException e) { throw new UnsupportedImageException(e.Message, format, e); }
    }

    /// <summary>EXIF with its orientation reset, plus the ICC profile unless the pixels were converted away from it (or it is not an RGB profile).</summary>
    private static ImageMetadata? BuildOutputMetadata(ImageMetadata? source, bool converted)
    {
        if (source == null) return null;
        var exif = source.Exif == null ? null : ExifOrientation.Normalize(source.Exif);
        var icc = !converted && source.IccProfile != null && SrgbConverter.ColorSpace(source.IccProfile) == "RGB " ? source.IccProfile : null;
        return exif == null && icc == null ? null : new ImageMetadata(exif, icc);
    }

    /// <summary>The requested crop, then rotation and mirroring.</summary>
    private static RgbaImage ApplyEdits(RgbaImage image, ImageCompressionOptions options, ref bool edited)
    {
        if (options.Crop is { } crop)
        {
            image = Editor.Crop(image, crop);
            edited = true;
        }
        if (options.Rotate != Rotation.None || options.FlipHorizontal || options.FlipVertical)
        {
            image = Editor.Transform(image, options.Rotate, options.FlipHorizontal, options.FlipVertical);
            edited = true;
        }
        return image;
    }

    /// <summary>The size to start from: the image itself, or scaled down (never up) to fit inside MaxWidth × MaxHeight.</summary>
    private static (int Width, int Height) FitBox(int width, int height, ImageCompressionOptions options, ref bool edited)
    {
        var box = Math.Min(options.MaxWidth > 0 ? (double)options.MaxWidth / width : 1, options.MaxHeight > 0 ? (double)options.MaxHeight / height : 1);
        if (box >= 1) return (width, height);
        edited = true;
        return (Math.Min(options.MaxWidth > 0 ? options.MaxWidth : int.MaxValue, Math.Max(1, (int)Math.Round(width * box))),
                Math.Min(options.MaxHeight > 0 ? options.MaxHeight : int.MaxValue, Math.Max(1, (int)Math.Round(height * box))));
    }

    // Palette sizes for an animated GIF, richest first. Plays the part JPEG quality does: stepped down one rung per pass before pixels are dropped.
    private static readonly int[] GifPalettes = [256, 192, 128, 96, 64, 48, 32, 24, 16];

    private static CompressedImage CompressAnimation(GifAnimation animation, byte[]? untouched, ImageCompressionOptions options, CancellationToken ct)
    {
        var edited = false;
        var frames = new List<GifFrame>();
        foreach (var f in animation.Frames) frames.Add(new GifFrame(ApplyEdits(f.Image, options, ref edited), f.DelayCentiseconds));
        var (width, height) = FitBox(frames[0].Image.Width, frames[0].Image.Height, options, ref edited);
        if (untouched != null && !edited)
            return new CompressedImage(new MemoryStream(untouched), "image/gif", ".gif", false);

        var lastRung = Math.Min(GifPalettes.Length - 1, Math.Max(0, (options.InitialQuality - options.MinQuality + 9) / 10));
        var rung = 0;
        var originalWidth = frames[0].Image.Width;

        for (var pass = 1; ; pass++)
        {
            ct.ThrowIfCancellationRequested();
            var current = width >= originalWidth && height >= frames[0].Image.Height
                ? frames
                : frames.Select(f => new GifFrame(Resizer.Shrink(f.Image, width, height, options.Filter), f.DelayCentiseconds)).ToList();
            var output = GifEncoder.Encode(current, animation.Loop, GifPalettes[rung]);

            var smallest = rung >= lastRung && width <= options.MinWidth;
            if (output.Length <= options.TargetSizeBytes || smallest || pass >= options.MaxPasses)
                return new CompressedImage(output, "image/gif", ".gif", true);

            output.Dispose();
            if (rung < lastRung) rung++;
            else
            {
                var nextWidth = Math.Max(Math.Min(options.MinWidth, width), Math.Max(1, (int)(width * options.ShrinkFactor)));
                height = Math.Max(1, (int)Math.Round(height * (nextWidth / (double)width)));
                width = nextWidth;
            }
        }
    }

    private static CompressedImage CompressCore(byte[] data, ImageFormat format, ImageCompressionOptions options, CancellationToken ct)
    {
        if (format == ImageFormat.Unknown) throw new InvalidDataException("The stream is not a recognised image.");

        var small = data.Length <= options.TargetSizeBytes;
        if (small && !options.HasEdits)
            return new CompressedImage(new MemoryStream(data), ImageFormatDetector.ContentType(format), ImageFormatDetector.Extension(format), false);
        var untouched = small ? data : null; // a small file the edits turn out not to change goes back exactly as it came

        RgbaImage original;
        var orientation = 1;
        switch (format)
        {
            case ImageFormat.Png:
                original = PngDecoder.Decode(data, options.MaxPixels);
                break;
            case ImageFormat.Jpeg:
                original = JpegDecoder.Decode(data, options.MaxPixels, out orientation);
                break;
            case ImageFormat.Gif:
                var animation = GifDecoder.DecodeAnimation(data, options.MaxPixels);
                if (animation.Frames.Count > 1 && options.Output == OutputFormat.Auto)
                    return CompressAnimation(animation, untouched, options, ct);
                original = animation.Frames[0].Image; // a still, or the first frame when JPEG/PNG was asked for explicitly
                break;
            case ImageFormat.Bmp:
                original = BmpDecoder.Decode(data, options.MaxPixels);
                break;
            case ImageFormat.Tiff:
                original = TiffDecoder.Decode(data, options.MaxPixels, out orientation);
                break;
            case ImageFormat.Tga:
                original = TgaDecoder.Decode(data, options.MaxPixels);
                break;
            case ImageFormat.Pnm:
                original = PnmDecoder.Decode(data, options.MaxPixels);
                break;
            case ImageFormat.Qoi:
                original = QoiDecoder.Decode(data, options.MaxPixels);
                break;
            case ImageFormat.WebP:
                original = WebPDecoder.Decode(data, options.MaxPixels, out orientation);
                break;
            default:
                throw new NotSupportedException($"A {format} image can be detected but not compressed; only JPEG, PNG, GIF, BMP, TIFF, TGA, PNM, QOI and still WebP are supported.");
        }
        data = [];

        // Colour: bring an embedded RGB profile's pixels to sRGB, so dropping the profile does not shift colours.
        var source = original.Metadata;
        var converted = false;
        if (options.ConvertToSrgb && source?.IccProfile is { } profile && SrgbConverter.TryCreate(profile) is { } converter)
        {
            converter.Apply(original.Pixels);
            converted = true;
        }
        var metadata = options.KeepMetadata ? BuildOutputMetadata(source, converted) : null;
        var exifOrientation = orientation;
        original = Orienter.Apply(original, exifOrientation);

        var edited = false;
        original = ApplyEdits(original, options, ref edited);

        var keepAlpha = options.Output switch
        {
            OutputFormat.Png => true,
            OutputFormat.Jpeg => false,
            _ => original.HasTransparency()
        };
        if (!keepAlpha) original = original.HasTransparency() ? original.FlattenOnWhite() : original;
        var quality = options.InitialQuality;
        var width = original.Width;
        var height = original.Height;
        (width, height) = FitBox(width, height, options, ref edited);
        if (untouched != null && !edited)
            return new CompressedImage(new MemoryStream(untouched), ImageFormatDetector.ContentType(format), ImageFormatDetector.Extension(format), false);
        if (!keepAlpha && Math.Max(width, height) > JpegEncoder.MaxDimension)
        {
            var fit = (double)JpegEncoder.MaxDimension / Math.Max(width, height);
            width = Math.Max(1, (int)(width * fit));
            height = Math.Max(1, (int)(height * fit));
        }

        for (var pass = 1; ; pass++)
        {
            ct.ThrowIfCancellationRequested();
            var current = Resizer.Shrink(original, width, height, options.Filter);
            var output = keepAlpha ? PngEncoder.Encode(current, metadata) : JpegEncoder.Encode(current, quality, metadata);

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
                // never step below MinWidth: the last step lands exactly on it, keeping the aspect ratio
                var nextWidth = Math.Max(Math.Min(options.MinWidth, width), Math.Max(1, (int)(width * options.ShrinkFactor)));
                height = Math.Max(1, (int)Math.Round(height * (nextWidth / (double)width)));
                width = nextWidth;
            }
        }
    }
}
