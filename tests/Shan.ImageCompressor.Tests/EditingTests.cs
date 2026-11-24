using Shan.ImageCompressor.Jpeg;
using Shan.ImageCompressor.Png;
using Shan.ImageCompressor.Processing;

namespace Shan.ImageCompressor.Tests;

public class EditingTests
{
    // 3 wide × 2 tall:  0 1 2
    //                   3 4 5
    private static RgbaImage Numbered(int width, int height)
    {
        var image = RgbaImage.Create(width, height);
        for (var i = 0; i < width * height; i++) { image.Pixels[i * 4] = (byte)i; image.Pixels[i * 4 + 3] = 255; }
        return image;
    }

    private static int[] Reds(RgbaImage image) => Enumerable.Range(0, image.Width * image.Height).Select(i => (int)image.Pixels[i * 4]).ToArray();

    private static byte[] Bytes(CompressedImage result) => ((MemoryStream)result.Content).ToArray();

    private static RgbaImage DecodePng(CompressedImage result) => PngDecoder.Decode(Bytes(result), 10_000_000);

    [Fact]
    public void Crop_Keeps_The_Rectangle_And_Clips_At_The_Edge()
    {
        var source = Numbered(3, 2);
        Assert.Equal([1, 2, 4, 5], Reds(Editor.Crop(source, new CropRegion(1, 0, 2, 2))));
        Assert.Equal([5], Reds(Editor.Crop(source, new CropRegion(2, 1, 50, 50))));
        Assert.Same(source, Editor.Crop(source, new CropRegion(0, 0, 3, 2)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Editor.Crop(source, new CropRegion(3, 0, 1, 1)));
    }

    [Theory]
    [InlineData(Rotation.None, false, false, new[] { 0, 1, 2, 3, 4, 5 })]
    [InlineData(Rotation.Rotate90, false, false, new[] { 3, 0, 4, 1, 5, 2 })]
    [InlineData(Rotation.Rotate180, false, false, new[] { 5, 4, 3, 2, 1, 0 })]
    [InlineData(Rotation.Rotate270, false, false, new[] { 2, 5, 1, 4, 0, 3 })]
    [InlineData(Rotation.None, true, false, new[] { 2, 1, 0, 5, 4, 3 })]
    [InlineData(Rotation.None, false, true, new[] { 3, 4, 5, 0, 1, 2 })]
    [InlineData(Rotation.None, true, true, new[] { 5, 4, 3, 2, 1, 0 })]
    [InlineData(Rotation.Rotate90, true, false, new[] { 0, 3, 1, 4, 2, 5 })]
    public void Rotation_And_Mirroring_Turn_The_Pixels(Rotation rotation, bool flipH, bool flipV, int[] expected)
    {
        Assert.Equal(expected, Reds(Editor.Transform(Numbered(3, 2), rotation, flipH, flipV)));
    }

    [Fact]
    public void A_Crop_Through_The_Pipeline_Keeps_Exactly_Those_Pixels()
    {
        var source = TestImages.Noisy(100, 100);
        var png = PngEncoder.Encode(source).ToArray();
        var result = ImageCompressor.Compress(png, new ImageCompressionOptions { Crop = new CropRegion(10, 20, 30, 40), Output = OutputFormat.Png });

        Assert.True(result.WasCompressed);
        var decoded = DecodePng(result);
        Assert.Equal((30, 40), (decoded.Width, decoded.Height));
        Assert.Equal(Reds(Editor.Crop(source, new CropRegion(10, 20, 30, 40))), Reds(decoded));
    }

    [Fact]
    public void Rotation_Through_The_Pipeline_Swaps_The_Dimensions()
    {
        var png = PngEncoder.Encode(TestImages.Noisy(120, 80)).ToArray();
        var result = ImageCompressor.Compress(png, new ImageCompressionOptions { Rotate = Rotation.Rotate90, Output = OutputFormat.Png });
        var decoded = DecodePng(result);
        Assert.Equal((80, 120), (decoded.Width, decoded.Height));
    }

    [Theory]
    [InlineData(400, 0, 400, 300)]
    [InlineData(0, 150, 200, 150)]
    [InlineData(400, 100, 133, 100)]
    public void Max_Width_And_Height_Fit_The_Image_Inside_The_Box(int maxWidth, int maxHeight, int expectedWidth, int expectedHeight)
    {
        var png = PngEncoder.Encode(TestImages.Noisy(800, 600, transparent: true)).ToArray();
        // a huge target: the file counts as small, but the size limit still has to be applied
        var result = ImageCompressor.Compress(png, new ImageCompressionOptions { TargetSizeBytes = 100_000_000, MaxWidth = maxWidth, MaxHeight = maxHeight });

        Assert.True(result.WasCompressed);
        var decoded = DecodePng(result);
        Assert.Equal((expectedWidth, expectedHeight), (decoded.Width, decoded.Height));
    }

    [Fact]
    public void A_Small_File_That_Already_Fits_The_Box_Is_Returned_Untouched()
    {
        var png = PngEncoder.Encode(TestImages.Noisy(100, 100)).ToArray();
        var result = ImageCompressor.Compress(png, new ImageCompressionOptions { MaxWidth = 500, MaxHeight = 500 });
        Assert.False(result.WasCompressed);
        Assert.Equal(png, Bytes(result));
    }

    [Fact]
    public void A_Limit_Is_Never_An_Enlargement()
    {
        var png = PngEncoder.Encode(TestImages.Noisy(100, 100, transparent: true)).ToArray();
        var result = ImageCompressor.Compress(png, new ImageCompressionOptions { TargetSizeBytes = 1000, MaxWidth = 5000, MinWidth = 1 });
        Assert.True(DecodePng(result).Width <= 100);
    }

    [Fact]
    public void A_Jpeg_Result_Respects_The_Box_Too()
    {
        var png = PngEncoder.Encode(TestImages.Noisy(600, 400)).ToArray();
        var result = ImageCompressor.Compress(png, new ImageCompressionOptions { TargetSizeBytes = 100_000_000, MaxWidth = 300 });
        var decoded = JpegDecoder.Decode(Bytes(result), 10_000_000, out _);
        Assert.Equal((300, 200), (decoded.Width, decoded.Height));
    }

    [Theory]
    [InlineData(ResizeFilter.Box)]
    [InlineData(ResizeFilter.Triangle)]
    [InlineData(ResizeFilter.CatmullRom)]
    [InlineData(ResizeFilter.Lanczos3)]
    public void Every_Filter_Keeps_A_Flat_Colour_Flat(ResizeFilter filter)
    {
        var source = RgbaImage.Create(64, 48);
        for (var i = 0; i < 64 * 48; i++) { source.Pixels[i * 4] = 200; source.Pixels[i * 4 + 1] = 100; source.Pixels[i * 4 + 2] = 30; source.Pixels[i * 4 + 3] = 255; }

        var result = Resizer.Shrink(source, 10, 7, filter);

        Assert.Equal((10, 7), (result.Width, result.Height));
        for (var i = 0; i < 10 * 7; i++)
            Assert.Equal(new byte[] { 200, 100, 30, 255 }, result.Pixels.AsSpan(i * 4, 4).ToArray());
    }

    [Theory]
    [InlineData(ResizeFilter.Triangle)]
    [InlineData(ResizeFilter.CatmullRom)]
    [InlineData(ResizeFilter.Lanczos3)]
    public void Sharper_Filters_Stay_Inside_The_Colour_Range_On_A_Hard_Edge(ResizeFilter filter)
    {
        var source = RgbaImage.Create(40, 40);
        for (var y = 0; y < 40; y++)
            for (var x = 0; x < 40; x++)
            {
                var o = (y * 40 + x) * 4;
                source.Pixels[o] = source.Pixels[o + 1] = source.Pixels[o + 2] = (byte)(x < 20 ? 0 : 255);
                source.Pixels[o + 3] = 255;
            }

        var result = Resizer.Shrink(source, 13, 13, filter);

        Assert.Equal(0, result.Pixels[0]);                       // far left stays black
        Assert.Equal(255, result.Pixels[(13 - 1) * 4]);          // far right stays white
        Assert.All(Enumerable.Range(0, 13 * 13), i => Assert.Equal(255, result.Pixels[i * 4 + 3]));
    }

    [Theory]
    [InlineData(ResizeFilter.Triangle)]
    [InlineData(ResizeFilter.Lanczos3)]
    public void Filters_Do_Not_Bleed_Colour_From_Transparent_Pixels(ResizeFilter filter)
    {
        var source = RgbaImage.Create(8, 1);
        for (var x = 0; x < 8; x++)
        {
            var o = x * 4;
            if (x < 4) { source.Pixels[o] = 255; source.Pixels[o + 3] = 255; } // opaque red on the left
            else { source.Pixels[o + 1] = 255; source.Pixels[o + 3] = 0; }     // invisible green on the right
        }

        var result = Resizer.Shrink(source, 2, 1, filter);

        Assert.Equal(255, result.Pixels[0]);
        Assert.Equal(0, result.Pixels[1]); // no green leaked into the red pixel
    }

    [Theory]
    [InlineData(ResizeFilter.Triangle)]
    [InlineData(ResizeFilter.Lanczos3)]
    public void A_Filter_Gives_The_Same_Output_On_One_Core_And_Many(ResizeFilter filter)
    {
        var png = PngEncoder.Encode(TestImages.Noisy(500, 400)).ToArray();
        var single = ImageCompressor.Compress(png, new ImageCompressionOptions { TargetSizeBytes = 20_000, Filter = filter, MaxDegreeOfParallelism = 1 });
        var many = ImageCompressor.Compress(png, new ImageCompressionOptions { TargetSizeBytes = 20_000, Filter = filter, MaxDegreeOfParallelism = 0 });
        Assert.Equal(Bytes(single), Bytes(many));
    }

    [Theory]
    [InlineData(nameof(ImageCompressionOptions.MaxWidth), -1)]
    [InlineData(nameof(ImageCompressionOptions.MaxHeight), -1)]
    [InlineData(nameof(ImageCompressionOptions.Rotate), 45)]
    [InlineData(nameof(ImageCompressionOptions.Filter), 99)]
    public void Unusable_Editing_Options_Are_Rejected_Up_Front(string property, int value)
    {
        var options = new ImageCompressionOptions();
        var info = typeof(ImageCompressionOptions).GetProperty(property)!;
        info.SetValue(options, info.PropertyType.IsEnum ? Enum.ToObject(info.PropertyType, value) : value);

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => ImageCompressor.Compress(new byte[16], options));
        Assert.Equal(property, ex.ParamName);
    }

    [Theory]
    [InlineData(-1, 0, 5, 5)]
    [InlineData(0, -1, 5, 5)]
    [InlineData(0, 0, 0, 5)]
    [InlineData(0, 0, 5, 0)]
    public void A_Malformed_Crop_Is_Rejected_Up_Front(int x, int y, int width, int height)
    {
        var options = new ImageCompressionOptions { Crop = new CropRegion(x, y, width, height) };
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => ImageCompressor.Compress(new byte[16], options));
        Assert.Equal(nameof(ImageCompressionOptions.Crop), ex.ParamName);
    }

    [Fact]
    public void A_Crop_Starting_Outside_The_Image_Is_An_Argument_Error()
    {
        var png = PngEncoder.Encode(TestImages.Noisy(50, 50)).ToArray();
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            ImageCompressor.Compress(png, new ImageCompressionOptions { Crop = new CropRegion(50, 0, 10, 10) }));
        Assert.Equal(nameof(ImageCompressionOptions.Crop), ex.ParamName);
    }
}
