// Runnable tour of Shan.ImageCompressor.   dotnet run --project samples/Shan.ImageCompressor.Examples [path-to-an-image]
// With no argument it builds a 1600x1200 test picture in memory, so nothing needs to be on disk.

using Shan.ImageCompressor;
using Shan.ImageCompressor.Examples;

var outputDir = Path.Combine(Path.GetTempPath(), "Shan.ImageCompressor.Examples");
Directory.CreateDirectory(outputDir);

var input = args.Length > 0 ? File.ReadAllBytes(args[0]) : SamplePicture.Bmp(1600, 1200);
Console.WriteLine($"Input: {Describe(input)}   (output goes to {outputDir})\n");

// 1. The basics -------------------------------------------------------------------------------------------------------
// Anything at or below TargetSizeBytes (default 768 KB) comes back untouched. Above it, the image is re-encoded smaller.
Section("1. Basic compression");
await using (var file = new MemoryStream(input))
{
    var result = await ImageCompressor.CompressAsync(file, new ImageCompressionOptions { TargetSizeBytes = 150 * 1024 });
    Show("150 KB target", result);
    Save(result, "basic");
}

// 2. Resizing ---------------------------------------------------------------------------------------------------------
// There is no "resize to W x H" call: the library shrinks by itself until the file fits. You steer it with four options:
//   InitialQuality / MinQuality - JPEG quality steps down by 10 from the first to the second before any pixels are dropped
//   ShrinkFactor                - then both dimensions are multiplied by this each pass (0.8 = 20 % smaller per pass)
//   MinWidth                    - the width never goes below this
//   MaxPasses                   - hard stop on the number of attempts
Section("2. Resizing - how the options steer it");
foreach (var (label, options) in new (string, ImageCompressionOptions)[]
{
    ("small target, default shrink", new() { TargetSizeBytes = 40 * 1024 }),
    ("small target, gentle shrink (0.95)", new() { TargetSizeBytes = 40 * 1024, ShrinkFactor = 0.95 }),
    ("small target, never below 1000 px wide", new() { TargetSizeBytes = 40 * 1024, MinWidth = 1000 }),
    ("keep quality high, shrink pixels instead", new() { TargetSizeBytes = 40 * 1024, InitialQuality = 90, MinQuality = 90 }),
    ("tiny target, but only 3 passes", new() { TargetSizeBytes = 5 * 1024, MaxPasses = 3 }),
})
{
    var result = ImageCompressor.Compress(input, options);
    Show(label, result);
}

// 3. Choosing the output format -----------------------------------------------------------------------------------------
// Auto (default): JPEG for opaque images, PNG when there is real transparency. Force one if you need to.
Section("3. Output format");
foreach (var format in new[] { OutputFormat.Auto, OutputFormat.Jpeg, OutputFormat.Png })
    Show($"Output = {format}", ImageCompressor.Compress(input, new ImageCompressionOptions { TargetSizeBytes = 200 * 1024, Output = format }));

// 4. Editing ----------------------------------------------------------------------------------------------------------
// MaxWidth/MaxHeight, Crop, Rotate and the flips change the pixels, so they apply even to a file that is already small.
// A picture that already fits the box (and has no other edit) still comes back untouched.
Section("4. Editing - fit a box, crop, rotate, mirror, filters");
foreach (var (label, options) in new (string, ImageCompressionOptions)[]
{
    ("fit inside 800 x 800", new() { MaxWidth = 800, MaxHeight = 800 }),
    ("crop 800 x 600 from (400, 300)", new() { Crop = new CropRegion(400, 300, 800, 600) }),
    ("rotate 90 degrees clockwise", new() { Rotate = Rotation.Rotate90 }),
    ("mirror left to right", new() { FlipHorizontal = true }),
    ("crop, then fit 400 wide with Lanczos", new() { Crop = new CropRegion(0, 0, 1200, 900), MaxWidth = 400, Filter = ResizeFilter.Lanczos3 }),
    ("already inside 5000 x 5000: not resized", new() { MaxWidth = 5000, MaxHeight = 5000 }),
})
{
    try { Show(label, ImageCompressor.Compress(input, options)); }
    catch (ArgumentOutOfRangeException) { Console.WriteLine($"  {label,-44} -> (this image is too small for that crop)"); }
}

// 5. Animated GIF -----------------------------------------------------------------------------------------------------
// An animated GIF stays animated: every frame, its delay and the loop count are kept. The palette steps down from 256 colours
// before pixels are dropped. (A small sample is built in, so a target of 1 byte forces the work.)
Section("5. Animated GIF");
var gif = Convert.FromBase64String(SampleAssets.AnimatedGif);
Console.WriteLine($"  input: {Describe(gif)}");
Show("TargetSizeBytes = 1: fewer colours, then smaller", ImageCompressor.Compress(gif, new ImageCompressionOptions { TargetSizeBytes = 1, MinWidth = 20 }));
Show("keep 256 colours, resize instead", ImageCompressor.Compress(gif, new ImageCompressionOptions { TargetSizeBytes = 1, MinWidth = 20, MinQuality = 90 }));
Show("MaxWidth = 20 (applied to every frame)", ImageCompressor.Compress(gif, new ImageCompressionOptions { MaxWidth = 20 }));
Show("Output = Png: the first frame only", ImageCompressor.Compress(gif, new ImageCompressionOptions { TargetSizeBytes = 1, Output = OutputFormat.Png }));

// 6. WebP -------------------------------------------------------------------------------------------------------------
// Lossy and lossless stills are read (with transparency, ICC profile and EXIF). They are written as JPEG, or PNG when transparent.
Section("6. WebP");
var webp = Convert.FromBase64String(SampleAssets.WebP);
Console.WriteLine($"  input: {Describe(webp)}, {webp.Length} bytes");
Show("WebP -> JPEG", ImageCompressor.Compress(webp, new ImageCompressionOptions { TargetSizeBytes = 1, MinWidth = 16 }));
Show("WebP -> PNG", ImageCompressor.Compress(webp, new ImageCompressionOptions { TargetSizeBytes = 1, MinWidth = 16, Output = OutputFormat.Png }));

// 7. Metadata and colour ----------------------------------------------------------------------------------------------
// By default all metadata (EXIF, GPS, XMP) is dropped, and pixels from an embedded Adobe RGB / Display P3 profile are
// converted to sRGB first so colours survive. Both can be changed:
Section("7. Metadata and colour");
var photo = args.Length > 0 ? input : null; // the generated BMP has no metadata, so this only shows with your own photo
if (photo is null) Console.WriteLine("  (pass a JPEG with EXIF/ICC to see this section do something)");
else
{
    Show("default: metadata dropped", ImageCompressor.Compress(photo, new ImageCompressionOptions { TargetSizeBytes = 1 }));
    Show("KeepMetadata (EXIF kept, orientation reset)", ImageCompressor.Compress(photo, new ImageCompressionOptions { TargetSizeBytes = 1, KeepMetadata = true }));
    Show("KeepMetadata, no sRGB conversion (ICC kept)", ImageCompressor.Compress(photo, new ImageCompressionOptions { TargetSizeBytes = 1, KeepMetadata = true, ConvertToSrgb = false }));
}

// 5. Detecting a format -----------------------------------------------------------------------------------------------
// From the bytes, never from a client-supplied content type or file name.
Section("8. Format detection");
Console.WriteLine($"  DetectFormat(bytes)            -> {ImageCompressor.DetectFormat(input)}");
await using (var stream = new MemoryStream(input))
    Console.WriteLine($"  DetectFormatAsync(stream)      -> {await ImageCompressor.DetectFormatAsync(stream)}");
Console.WriteLine($"  DetectFormat(\"hello\"u8)        -> {ImageCompressor.DetectFormat("hello"u8)}");

// 6. Async, streams, cancellation and parallelism ---------------------------------------------------------------------
Section("9. Streams, cancellation, parallelism");
using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
await using (var stream = new MemoryStream(input))
{
    // MaxDegreeOfParallelism: 0 (default) uses every core; 1 keeps one call on one core, for busy servers. Output is identical.
    var result = await ImageCompressor.CompressAsync(stream, new ImageCompressionOptions { TargetSizeBytes = 100 * 1024, MaxDegreeOfParallelism = 1 }, cts.Token);
    Show("async, one core, cancellable", result);
    Save(result, "async");
}

// 7. Handling errors ----------------------------------------------------------------------------------------------------
// Whatever the bytes, you get a result or one of two exceptions (both derive from ImageCompressionException), and each carries
// the detected format. A bad option throws ArgumentOutOfRangeException before any work starts.
Section("10. Errors");
await TryAsync("not an image", () => ImageCompressor.Compress("this is not a picture"u8.ToArray()));
await TryAsync("truncated image", () => ImageCompressor.Compress(input[..(input.Length / 3)], new ImageCompressionOptions { TargetSizeBytes = 1 }));
await TryAsync("too many pixels", () => ImageCompressor.Compress(input, new ImageCompressionOptions { TargetSizeBytes = 1, MaxPixels = 10_000 }));
await TryAsync("animated WebP (detected, not supported)", () =>
{
    var animated = new byte[100]; // a WebP container whose header says "animated"
    "RIFF"u8.CopyTo(animated);
    "WEBPVP8X"u8.CopyTo(animated.AsSpan(8));
    animated[4] = 92; animated[16] = 10; animated[20] = 0x02;
    return ImageCompressor.Compress(animated, new ImageCompressionOptions { TargetSizeBytes = 1 });
});
await TryAsync("bad option", () => ImageCompressor.Compress(input, new ImageCompressionOptions { ShrinkFactor = 1.5 }));

// A common pattern: compress when possible, keep the original when the format is not supported.
Section("11. Compress, or keep the original");
byte[] stored;
try { stored = ((MemoryStream)ImageCompressor.Compress(AnimatedWebP(), new ImageCompressionOptions { TargetSizeBytes = 1 }).Content).ToArray(); }
catch (UnsupportedImageException e) { stored = []; Console.WriteLine($"  kept the original because {e.Format} cannot be compressed here"); }

Console.WriteLine($"\nWrote the compressed files to {outputDir}");
return;

// ---------------------------------------------------------------------------------------------------------------------------

void Section(string title) => Console.WriteLine($"\n{title}\n{new string('-', title.Length)}");

void Show(string label, CompressedImage result)
{
    var bytes = ((MemoryStream)result.Content).ToArray();
    Console.WriteLine($"  {label,-44} -> {Size(bytes.Length),8}  {Describe(bytes),-24} {(result.WasCompressed ? "" : "(returned as-is)")}");
}

static string Size(int bytes) => bytes < 10 * 1024 ? $"{bytes} B" : $"{bytes / 1024} KB";

void Save(CompressedImage result, string name)
{
    var path = Path.Combine(outputDir, name + result.Extension);
    File.WriteAllBytes(path, ((MemoryStream)result.Content).ToArray());
}

async Task TryAsync(string label, Func<CompressedImage> action)
{
    try { action(); Console.WriteLine($"  {label,-34} -> ok"); }
    catch (InvalidImageException e) { Console.WriteLine($"  {label,-34} -> InvalidImageException (format {e.Format}): {e.Message}"); }
    catch (UnsupportedImageException e) { Console.WriteLine($"  {label,-34} -> UnsupportedImageException (format {e.Format}): {e.Message}"); }
    catch (ArgumentOutOfRangeException e) { Console.WriteLine($"  {label,-34} -> ArgumentOutOfRangeException ({e.ParamName})"); }
    await Task.CompletedTask;
}

// "FORMAT WxH" read straight from the headers, so the examples can show how resizing changed the dimensions.
static string Describe(byte[] data)
{
    var format = ImageCompressor.DetectFormat(data);
    var (w, h) = format switch
    {
        ImageFormat.Png when data.Length > 24 => (ReadInt(data, 16, bigEndian: true), ReadInt(data, 20, bigEndian: true)),
        ImageFormat.Bmp when data.Length > 26 => (ReadInt(data, 18, bigEndian: false), Math.Abs(ReadInt(data, 22, bigEndian: false))),
        ImageFormat.Jpeg => JpegSize(data),
        ImageFormat.Gif => (data[6] | data[7] << 8, data[8] | data[9] << 8),
        ImageFormat.WebP when data.Length > 30 && data[12] == (byte)'V' && data[15] == (byte)' ' => ((data[26] | data[27] << 8) & 0x3FFF, (data[28] | data[29] << 8) & 0x3FFF),
        _ => (0, 0)
    };
    var frames = format == ImageFormat.Gif ? $", {GifFrames(data)} frame(s)" : "";
    return w > 0 ? $"{format} {w}x{h}{frames}" : format.ToString();
}

// An animated WebP header with no frames in it: recognised, but not something this library can decode.
static byte[] AnimatedWebP()
{
    var bytes = new byte[100];
    "RIFF"u8.CopyTo(bytes);
    "WEBPVP8X"u8.CopyTo(bytes.AsSpan(8));
    bytes[4] = 92; bytes[16] = 10; bytes[20] = 0x02;
    return bytes;
}

// Counts the image descriptors of a GIF by walking its blocks.
static int GifFrames(byte[] d)
{
    var pos = 13;
    if ((d[10] & 0x80) != 0) pos += 3 * (1 << ((d[10] & 7) + 1));
    var frames = 0;
    while (pos < d.Length && d[pos] != 0x3B)
    {
        var block = d[pos++];
        if (block == 0x21) pos++; // extension label
        else if (block == 0x2C)
        {
            frames++;
            var flags = d[pos + 8];
            pos += 9;
            if ((flags & 0x80) != 0) pos += 3 * (1 << ((flags & 7) + 1));
            pos++; // LZW minimum code size
        }
        else break;
        while (pos < d.Length && d[pos] != 0) pos += d[pos] + 1; // sub-blocks
        pos++;
    }
    return frames;
}

static int ReadInt(byte[] d, int at, bool bigEndian) => bigEndian
    ? d[at] << 24 | d[at + 1] << 16 | d[at + 2] << 8 | d[at + 3]
    : d[at] | d[at + 1] << 8 | d[at + 2] << 16 | d[at + 3] << 24;

static (int, int) JpegSize(byte[] d)
{
    for (var i = 2; i + 9 < d.Length;)
    {
        if (d[i] != 0xFF) { i++; continue; }
        var marker = d[i + 1];
        if (marker is >= 0xC0 and <= 0xCF and not (0xC4 or 0xC8 or 0xCC)) return (d[i + 7] << 8 | d[i + 8], d[i + 5] << 8 | d[i + 6]);
        i += 2 + (d[i + 2] << 8 | d[i + 3]);
    }
    return (0, 0);
}

/// <summary>A detailed, slightly noisy picture, written as a plain 24-bit BMP (one of the formats the library reads).</summary>
static class SamplePicture
{
    public static byte[] Bmp(int width, int height)
    {
        var rowBytes = (width * 3 + 3) / 4 * 4;
        var data = new byte[54 + rowBytes * height];
        data[0] = (byte)'B'; data[1] = (byte)'M';
        BitConverter.GetBytes(data.Length).CopyTo(data, 2);
        BitConverter.GetBytes(54).CopyTo(data, 10);
        BitConverter.GetBytes(40).CopyTo(data, 14);
        BitConverter.GetBytes(width).CopyTo(data, 18);
        BitConverter.GetBytes(height).CopyTo(data, 22);
        BitConverter.GetBytes((short)1).CopyTo(data, 26);
        BitConverter.GetBytes((short)24).CopyTo(data, 28);

        var random = new Random(42);
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var o = 54 + y * rowBytes + x * 3;
                var ring = (int)(60 * Math.Sin(Math.Sqrt((x - width / 2.0) * (x - width / 2.0) + (y - height / 2.0) * (y - height / 2.0)) / 9));
                data[o] = (byte)Math.Clamp(y * 255 / height + ring + random.Next(-18, 18), 0, 255);
                data[o + 1] = (byte)Math.Clamp(x * 255 / width - ring + random.Next(-18, 18), 0, 255);
                data[o + 2] = (byte)Math.Clamp(128 + ring * 2 + random.Next(-18, 18), 0, 255);
            }
        return data;
    }
}
