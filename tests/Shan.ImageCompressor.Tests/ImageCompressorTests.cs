using Shan.ImageCompressor.Jpeg;
using Shan.ImageCompressor.Png;

namespace Shan.ImageCompressor.Tests;

public class ImageCompressorTests
{
    private static ImageCompressionOptions Target(long bytes) => new() { TargetSizeBytes = bytes };

    [Fact]
    public async Task Small_Image_Is_Returned_Unchanged()
    {
        using var input = TestImages.NoisyPng(16, false);
        var original = input.ToArray();
        var result = await ImageCompressor.CompressAsync(input);
        Assert.False(result.WasCompressed);
        Assert.Equal("image/png", result.ContentType);
        Assert.Equal(".png", result.Extension);
        Assert.Equal(original, ((MemoryStream)result.Content).ToArray());
    }

    [Fact]
    public async Task Large_Opaque_Png_Becomes_A_Smaller_Jpeg()
    {
        using var input = TestImages.NoisyPng(400, false);
        var result = await ImageCompressor.CompressAsync(input, Target(40 * 1024));
        Assert.True(result.WasCompressed);
        Assert.Equal("image/jpeg", result.ContentType);
        Assert.Equal(".jpg", result.Extension);
        Assert.True(result.Content.Length < input.Length);
        Assert.Equal(ImageFormat.Jpeg, await ImageCompressor.DetectFormatAsync(result.Content));
    }

    [Fact]
    public async Task Large_Transparent_Png_Stays_Png_And_Keeps_Its_Alpha()
    {
        using var input = TestImages.NoisyPng(800, true);
        var result = await ImageCompressor.CompressAsync(input, Target(100 * 1024));
        Assert.Equal("image/png", result.ContentType);
        var decoded = PngDecoder.Decode(((MemoryStream)result.Content).ToArray(), 10_000_000);
        Assert.True(decoded.HasTransparency());
        Assert.True(decoded.Width < 800); // only dimensions can shrink for PNG
    }

    [Fact]
    public async Task Jpeg_Input_Is_Recompressed_Below_The_Target()
    {
        using var source = new MemoryStream(JpegEncoder.Encode(TestImages.Noisy(500, 400), 95).ToArray());
        var result = await ImageCompressor.CompressAsync(source, new ImageCompressionOptions { TargetSizeBytes = 60 * 1024, MinWidth = 50 });
        Assert.True(result.WasCompressed);
        Assert.True(result.Content.Length <= 60 * 1024);
    }

    [Fact]
    public async Task Stops_At_The_Quality_And_Width_Floor()
    {
        using var input = TestImages.NoisyPng(600, false);
        var result = await ImageCompressor.CompressAsync(input, new ImageCompressionOptions { TargetSizeBytes = 1, MinWidth = 300 });
        var decoded = JpegDecoder.Decode(((MemoryStream)result.Content).ToArray(), 10_000_000, out _);
        Assert.InRange(decoded.Width, 240, 300); // an unreachable target ends at the width floor rather than looping forever
    }

    [Fact]
    public async Task Result_Is_Positioned_At_The_Start()
    {
        using var input = TestImages.NoisyPng(300, false);
        var result = await ImageCompressor.CompressAsync(input, Target(20 * 1024));
        Assert.Equal(0, result.Content.Position);
    }

    [Fact]
    public async Task Does_Not_Dispose_The_Input()
    {
        using var input = TestImages.NoisyPng(300, false);
        await ImageCompressor.CompressAsync(input, Target(20 * 1024));
        Assert.True(input.CanRead);
    }

    [Fact]
    public async Task Cancellation_Is_Honoured()
    {
        using var input = TestImages.NoisyPng(300, false);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ImageCompressor.CompressAsync(input, Target(20 * 1024), cts.Token));
    }

    [Fact]
    public async Task Non_Image_Throws_InvalidData()
    {
        using var input = new MemoryStream("not an image"u8.ToArray());
        await Assert.ThrowsAsync<InvalidImageException>(() => ImageCompressor.CompressAsync(input));
    }

    [Fact]
    public async Task Corrupt_Image_Throws_InvalidData()
    {
        using var input = new MemoryStream(TestImages.NoisyPng(300, false).ToArray()[..2000]);
        await Assert.ThrowsAsync<InvalidImageException>(() => ImageCompressor.CompressAsync(input, Target(1000)));
    }

    [Fact]
    public async Task Detected_But_Unsupported_Format_Is_Rejected_Only_When_It_Needs_Compressing()
    {
        var webp = new byte[5000];
        "RIFF"u8.CopyTo(webp);
        "WEBP"u8.CopyTo(webp.AsSpan(8));

        var small = await ImageCompressor.CompressAsync(new MemoryStream(webp), Target(10_000));
        Assert.False(small.WasCompressed);
        Assert.Equal("image/webp", small.ContentType);

        await Assert.ThrowsAsync<UnsupportedImageException>(() => ImageCompressor.CompressAsync(new MemoryStream(webp), Target(1000)));
    }

    [Fact]
    public async Task Pixel_Limit_Is_Enforced_Before_Decoding()
    {
        using var input = TestImages.NoisyPng(300, false);
        var options = new ImageCompressionOptions { TargetSizeBytes = 1000, MaxPixels = 1000 };
        var ex = await Assert.ThrowsAsync<InvalidImageException>(() => ImageCompressor.CompressAsync(input, options));
        Assert.Contains("limit", ex.Message);
    }

    [Fact]
    public async Task Works_On_A_NonSeekable_Stream()
    {
        using var inner = TestImages.NoisyPng(300, false);
        using var input = new ForwardOnlyStream(inner.ToArray());
        var result = await ImageCompressor.CompressAsync(input, Target(20 * 1024));
        Assert.True(result.WasCompressed);
    }

    private sealed class ForwardOnlyStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
    }
}
