namespace Shan.ImageCompressor.Tests;

public class ApiTests
{
    [Fact]
    public async Task Sync_And_Async_Give_The_Same_Result()
    {
        var png = TestImages.NoisyPng(300, false).ToArray();
        var options = new ImageCompressionOptions { TargetSizeBytes = 30 * 1024 };

        using var sync = ImageCompressor.Compress(new MemoryStream(png), options).Content;
        using var fromBytes = ImageCompressor.Compress(png, options).Content;
        var asyncResult = await ImageCompressor.CompressAsync(new MemoryStream(png), options);

        Assert.Equal(((MemoryStream)asyncResult.Content).ToArray(), ((MemoryStream)sync).ToArray());
        Assert.Equal(((MemoryStream)asyncResult.Content).ToArray(), ((MemoryStream)fromBytes).ToArray());
    }

    [Fact]
    public void Sync_Compress_Reads_From_The_Start_Of_A_Seekable_Stream()
    {
        var png = TestImages.NoisyPng(20, false);
        png.Position = 10;
        var result = ImageCompressor.Compress(png);
        Assert.False(result.WasCompressed);
        Assert.Equal(png.ToArray(), ((MemoryStream)result.Content).ToArray());
    }

    [Fact]
    public async Task Null_Input_Is_An_Argument_Error()
    {
        Assert.Throws<ArgumentNullException>(() => ImageCompressor.Compress((Stream)null!));
        Assert.Throws<ArgumentNullException>(() => ImageCompressor.Compress((byte[])null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => ImageCompressor.CompressAsync(null!));
    }

    [Theory]
    [InlineData(nameof(ImageCompressionOptions.TargetSizeBytes), 0)]
    [InlineData(nameof(ImageCompressionOptions.InitialQuality), 0)]
    [InlineData(nameof(ImageCompressionOptions.InitialQuality), 101)]
    [InlineData(nameof(ImageCompressionOptions.MinQuality), 0)]
    [InlineData(nameof(ImageCompressionOptions.MinWidth), 0)]
    [InlineData(nameof(ImageCompressionOptions.ShrinkFactor), 1.0)]
    [InlineData(nameof(ImageCompressionOptions.ShrinkFactor), 0.0)]
    [InlineData(nameof(ImageCompressionOptions.ShrinkFactor), double.NaN)]
    [InlineData(nameof(ImageCompressionOptions.MaxPasses), 0)]
    [InlineData(nameof(ImageCompressionOptions.MaxPixels), 0)]
    public void Unusable_Options_Are_Rejected_Up_Front(string property, double value)
    {
        var options = new ImageCompressionOptions();
        var info = typeof(ImageCompressionOptions).GetProperty(property)!;
        info.SetValue(options, Convert.ChangeType(value, info.PropertyType));

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => ImageCompressor.Compress(TestImages.NoisyPng(8, false).ToArray(), options));
        Assert.Equal(property, ex.ParamName);
    }

    [Fact]
    public async Task Cancellation_Stops_The_Shrink_Loop()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ImageCompressor.CompressAsync(TestImages.NoisyPng(200, false), new ImageCompressionOptions { TargetSizeBytes = 1 }, cts.Token));
    }
}
