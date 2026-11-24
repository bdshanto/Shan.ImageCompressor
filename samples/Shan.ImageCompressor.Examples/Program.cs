// Runnable tour of Shan.ImageCompressor.   dotnet run --project samples/Shan.ImageCompressor.Examples [path-to-an-image]
// With no argument it builds a 1600x1200 test picture in memory, so nothing needs to be on disk.

using Shan.ImageCompressor;

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

// 4. Metadata and colour ----------------------------------------------------------------------------------------------
// By default all metadata (EXIF, GPS, XMP) is dropped, and pixels from an embedded Adobe RGB / Display P3 profile are
// converted to sRGB first so colours survive. Both can be changed:
Section("4. Metadata and colour");
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
Section("5. Format detection");
Console.WriteLine($"  DetectFormat(bytes)            -> {ImageCompressor.DetectFormat(input)}");
await using (var stream = new MemoryStream(input))
    Console.WriteLine($"  DetectFormatAsync(stream)      -> {await ImageCompressor.DetectFormatAsync(stream)}");
Console.WriteLine($"  DetectFormat(\"hello\"u8)        -> {ImageCompressor.DetectFormat("hello"u8)}");

// 6. Async, streams, cancellation and parallelism ---------------------------------------------------------------------
Section("6. Streams, cancellation, parallelism");
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
Section("7. Errors");
await TryAsync("not an image", () => ImageCompressor.Compress("this is not a picture"u8.ToArray()));
await TryAsync("truncated image", () => ImageCompressor.Compress(input[..(input.Length / 3)], new ImageCompressionOptions { TargetSizeBytes = 1 }));
await TryAsync("too many pixels", () => ImageCompressor.Compress(input, new ImageCompressionOptions { TargetSizeBytes = 1, MaxPixels = 10_000 }));
await TryAsync("WebP (detected, not supported)", () =>
{
    var webp = new byte[100];
    "RIFF"u8.CopyTo(webp);
    "WEBP"u8.CopyTo(webp.AsSpan(8));
    return ImageCompressor.Compress(webp, new ImageCompressionOptions { TargetSizeBytes = 1 });
});
await TryAsync("bad option", () => ImageCompressor.Compress(input, new ImageCompressionOptions { ShrinkFactor = 1.5 }));

// A common pattern: compress when possible, keep the original when the format is not supported.
Section("8. Compress, or keep the original");
byte[] stored;
try { stored = ((MemoryStream)ImageCompressor.Compress(new byte[] { 0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, 0x57, 0x45, 0x42, 0x50 }.Concat(new byte[200]).ToArray(), new ImageCompressionOptions { TargetSizeBytes = 1 }).Content).ToArray(); }
catch (UnsupportedImageException e) { stored = []; Console.WriteLine($"  kept the original because {e.Format} cannot be compressed here"); }

Console.WriteLine($"\nWrote the compressed files to {outputDir}");
return;

// ---------------------------------------------------------------------------------------------------------------------------

void Section(string title) => Console.WriteLine($"\n{title}\n{new string('-', title.Length)}");

void Show(string label, CompressedImage result)
{
    var bytes = ((MemoryStream)result.Content).ToArray();
    Console.WriteLine($"  {label,-44} -> {bytes.Length / 1024,6} KB  {Describe(bytes),-24} {(result.WasCompressed ? "" : "(returned as-is)")}");
}

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
        _ => (0, 0)
    };
    return w > 0 ? $"{format} {w}x{h}" : format.ToString();
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
