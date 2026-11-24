using Shan.ImageCompressor.Gif;

namespace Shan.ImageCompressor.Tests;

public class AnimatedGifCompressionTests
{
    /// <summary>A bigger animation with plenty of colours (a gradient plus a moving block), so there is real work to do.</summary>
    private static byte[] Animation(int width, int height, int frames, bool transparentCorner = false)
    {
        var list = new List<GifFrame>();
        for (var f = 0; f < frames; f++)
        {
            var image = RgbaImage.Create(width, height);
            for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                {
                    var o = (y * width + x) * 4;
                    var block = x >= f * 4 && x < f * 4 + width / 5 && y >= height / 3 && y < height / 3 + height / 4;
                    image.Pixels[o] = (byte)(block ? 255 : x * 255 / width);
                    image.Pixels[o + 1] = (byte)(block ? 255 : y * 255 / height);
                    image.Pixels[o + 2] = (byte)(block ? 0 : (x + y + f * 9) % 256);
                    image.Pixels[o + 3] = (byte)(transparentCorner && x < width / 6 && y < height / 6 ? 0 : 255);
                }
            list.Add(new GifFrame(image, 6 + f % 3));
        }
        return GifEncoder.Encode(list, 0, 256).ToArray();
    }

    private static GifAnimation Decode(CompressedImage result) => GifDecoder.DecodeAnimation(((MemoryStream)result.Content).ToArray(), 100_000_000);

    [Fact]
    public void Compressing_An_Animation_Keeps_Every_Frame_Delay_And_The_Loop_Count()
    {
        var source = Animation(240, 160, 10);
        var original = GifDecoder.DecodeAnimation(source, 100_000_000);

        var result = ImageCompressor.Compress(source, new ImageCompressionOptions { TargetSizeBytes = source.Length / 3, MinWidth = 60 });
        var decoded = Decode(result);

        Assert.True(result.WasCompressed);
        Assert.Equal("image/gif", result.ContentType);
        Assert.True(result.Content.Length <= source.Length / 3, $"{result.Content.Length} is over the target");
        Assert.Equal(original.Frames.Count, decoded.Frames.Count);
        Assert.Equal(original.Frames.Select(f => f.DelayCentiseconds), decoded.Frames.Select(f => f.DelayCentiseconds));
        Assert.Equal(original.Loop, decoded.Loop);
    }

    [Fact]
    public void A_Tight_Target_Shrinks_The_Frames_Down_To_The_Width_Floor()
    {
        var source = Animation(240, 160, 8);
        var result = ImageCompressor.Compress(source, new ImageCompressionOptions { TargetSizeBytes = 1, MinWidth = 100 });
        var decoded = Decode(result);

        Assert.Equal(100, decoded.Width);
        Assert.All(decoded.Frames, f => Assert.Equal(100, f.Image.Width));
    }

    [Fact]
    public void An_Animation_Already_Small_Enough_Is_Returned_Untouched()
    {
        var source = Animation(60, 40, 4);
        var result = ImageCompressor.Compress(source);
        Assert.False(result.WasCompressed);
        Assert.Equal(source, ((MemoryStream)result.Content).ToArray());
    }

    [Fact]
    public void Max_Width_Resizes_Every_Frame_Even_When_The_File_Is_Small()
    {
        var source = Animation(120, 80, 4);
        var result = ImageCompressor.Compress(source, new ImageCompressionOptions { MaxWidth = 60 });
        var decoded = Decode(result);

        Assert.True(result.WasCompressed);
        Assert.Equal((60, 40), (decoded.Width, decoded.Height));
        Assert.Equal(4, decoded.Frames.Count);
    }

    [Fact]
    public void Crop_And_Rotation_Apply_To_Every_Frame()
    {
        var source = Animation(120, 80, 3);
        var result = ImageCompressor.Compress(source, new ImageCompressionOptions { Crop = new CropRegion(10, 10, 50, 30), Rotate = Rotation.Rotate90 });
        var decoded = Decode(result);

        Assert.Equal((30, 50), (decoded.Width, decoded.Height));
        Assert.Equal(3, decoded.Frames.Count);
    }

    [Fact]
    public void See_Through_Pixels_Survive_Compression()
    {
        var source = Animation(180, 120, 6, transparentCorner: true);
        var result = ImageCompressor.Compress(source, new ImageCompressionOptions { TargetSizeBytes = source.Length / 2, MinWidth = 180 });
        var decoded = Decode(result);

        Assert.All(decoded.Frames, f => Assert.Equal(0, f.Image.Pixels[3])); // the top-left pixel stays transparent
        Assert.All(decoded.Frames, f => Assert.Equal(255, f.Image.Pixels[((120 - 1) * 180 + 179) * 4 + 3]));
    }

    [Fact]
    public void Fixing_The_Palette_At_256_Colours_Keeps_Quality_And_Resizes_Instead()
    {
        var source = Animation(240, 160, 6);
        var fixedPalette = ImageCompressor.Compress(source, new ImageCompressionOptions { TargetSizeBytes = source.Length / 3, MinQuality = 90, InitialQuality = 90, MinWidth = 40 });
        var stepped = ImageCompressor.Compress(source, new ImageCompressionOptions { TargetSizeBytes = source.Length / 3, MinWidth = 40 });

        Assert.True(Decode(fixedPalette).Width <= Decode(stepped).Width, "keeping all colours should cost pixels");
    }

    [Fact]
    public void An_Animation_Gives_The_Same_Output_On_One_Core_And_Many()
    {
        var source = Animation(200, 120, 40);
        var single = ImageCompressor.Compress(source, new ImageCompressionOptions { TargetSizeBytes = 20_000, MinWidth = 50, MaxDegreeOfParallelism = 1 });
        var many = ImageCompressor.Compress(source, new ImageCompressionOptions { TargetSizeBytes = 20_000, MinWidth = 50, MaxDegreeOfParallelism = 0 });
        Assert.Equal(((MemoryStream)single.Content).ToArray(), ((MemoryStream)many.Content).ToArray());
    }

    [Fact]
    public void Too_Many_Frames_For_The_Pixel_Budget_Are_Refused_Before_They_Are_Held_In_Memory()
    {
        var source = Animation(100, 100, 30);
        var ex = Assert.Throws<InvalidImageException>(() =>
            ImageCompressor.Compress(source, new ImageCompressionOptions { TargetSizeBytes = 1, MaxPixels = 100 * 100 * 5 }));
        Assert.Equal(ImageFormat.Gif, ex.Format);
    }

    [Fact]
    public async Task Cancellation_Stops_An_Animation_Compression()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ImageCompressor.CompressAsync(new MemoryStream(Animation(120, 80, 4)), new ImageCompressionOptions { TargetSizeBytes = 1 }, cts.Token));
    }
}
