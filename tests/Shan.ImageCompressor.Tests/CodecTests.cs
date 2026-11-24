using Shan.ImageCompressor.Jpeg;
using Shan.ImageCompressor.Png;

namespace Shan.ImageCompressor.Tests;

/// <summary>The codecs checked against reference output from Pillow (see Fixtures.cs), plus round trips through our own encoders.</summary>
public class CodecTests
{
    [Theory]
    [InlineData(nameof(Fixtures.InterlacedRgba))]
    [InlineData(nameof(Fixtures.PaletteWithAlpha))]
    [InlineData(nameof(Fixtures.OneBitGray))]
    [InlineData(nameof(Fixtures.GrayAlpha))]
    [InlineData(nameof(Fixtures.ColorKey))]
    public void Png_Decodes_Exactly_Like_The_Reference(string name)
    {
        var fixture = Fixtures.Get(name);
        var image = PngDecoder.Decode(fixture.File, 1_000_000);
        Assert.Equal((fixture.Width, fixture.Height), (image.Width, image.Height));
        Assert.Equal(fixture.ExpectedRgba, image.Pixels);
    }

    [Theory]
    [InlineData(nameof(Fixtures.Jpeg420))]
    [InlineData(nameof(Fixtures.Jpeg444))]
    [InlineData(nameof(Fixtures.JpegGray))]
    public void Jpeg_Decodes_Within_Rounding_Of_The_Reference(string name)
    {
        var fixture = Fixtures.Get(name);
        var image = JpegDecoder.Decode(fixture.File, 1_000_000, out var orientation);
        Assert.Equal(1, orientation);
        Assert.Equal((fixture.Width, fixture.Height), (image.Width, image.Height));
        Assert.True(TestImages.MeanAbsoluteDifference(fixture.ExpectedRgba, image.Pixels) < 0.5);
        Assert.All(fixture.ExpectedRgba.Zip(image.Pixels), pair => Assert.InRange(Math.Abs(pair.First - pair.Second), 0, 4));
    }

    [Fact]
    public async Task Jpeg_Exif_Orientation_Is_Read_And_Applied()
    {
        var fixture = Fixtures.JpegOrientation6;
        var decoded = JpegDecoder.Decode(fixture.File, 1_000_000, out var orientation);
        Assert.Equal(6, orientation);
        Assert.Equal((13, 9), (decoded.Width, decoded.Height)); // the raw pixels are untouched

        var compressed = await ImageCompressor.CompressAsync(new MemoryStream(fixture.File), new ImageCompressionOptions { TargetSizeBytes = 1 });
        var output = JpegDecoder.Decode(((MemoryStream)compressed.Content).ToArray(), 1_000_000, out var outOrientation);
        Assert.Equal(1, outOrientation); // the orientation tag is gone because it was baked into the pixels
        Assert.Equal((fixture.Width, fixture.Height), (output.Width, output.Height));
        Assert.Equal((9, 13), (output.Width, output.Height));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Png_Round_Trips_Losslessly(bool transparent)
    {
        var source = TestImages.Noisy(50, 31, transparent);
        var decoded = PngDecoder.Decode(PngEncoder.Encode(source).ToArray(), 1_000_000);
        Assert.Equal(source.Pixels, decoded.Pixels);
    }

    [Theory]
    [InlineData(95)] // 4:4:4
    [InlineData(60)] // 4:2:0
    [InlineData(10)]
    public void Jpeg_Round_Trips_Approximately(int quality)
    {
        var source = Smooth(70, 45);
        var decoded = JpegDecoder.Decode(JpegEncoder.Encode(source, quality).ToArray(), 1_000_000, out _);
        Assert.Equal((70, 45), (decoded.Width, decoded.Height));
        var tolerance = quality >= 90 ? 3 : quality >= 50 ? 8 : 25;
        Assert.True(TestImages.MeanAbsoluteDifference(source.Pixels, decoded.Pixels, ignoreAlpha: true) < tolerance);
    }

    [Fact]
    public void Truncated_Data_Fails_Cleanly()
    {
        Assert.Throws<InvalidDataException>(() => PngDecoder.Decode(Fixtures.InterlacedRgba.File[..40], 1_000_000));
        Assert.Throws<InvalidDataException>(() => JpegDecoder.Decode(Fixtures.Jpeg420.File[..60], 1_000_000, out _));
    }

    [Fact]
    public void Garbage_Inside_The_Stream_Fails_Cleanly()
    {
        var random = new Random(3);
        foreach (var fixture in new[] { Fixtures.InterlacedRgba, Fixtures.Jpeg420 })
        {
            for (var trial = 0; trial < 50; trial++)
            {
                var bytes = fixture.File.ToArray();
                for (var i = 0; i < 4; i++) bytes[random.Next(8, bytes.Length)] = (byte)random.Next(256);
                try
                {
                    if (fixture.File[0] == 0xFF) JpegDecoder.Decode(bytes, 1_000_000, out _);
                    else PngDecoder.Decode(bytes, 1_000_000);
                }
                catch (Exception e) when (e is InvalidDataException or NotSupportedException) { }
            }
        }
    }

    private static RgbaImage Smooth(int width, int height)
    {
        var image = RgbaImage.Create(width, height);
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var o = (y * width + x) * 4;
                image.Pixels[o] = (byte)(x * 255 / width);
                image.Pixels[o + 1] = (byte)(y * 255 / height);
                image.Pixels[o + 2] = (byte)((x + y) * 255 / (width + height));
                image.Pixels[o + 3] = 255;
            }
        return image;
    }
}
