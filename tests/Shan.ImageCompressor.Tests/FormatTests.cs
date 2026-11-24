using Shan.ImageCompressor.Bmp;
using Shan.ImageCompressor.Gif;
using Shan.ImageCompressor.Jpeg;

namespace Shan.ImageCompressor.Tests;

/// <summary>Progressive JPEG, GIF and BMP decoding, checked against reference pixels (Pillow's decoder, or the source arrays of the hand-built BMPs).</summary>
public class FormatTests
{
    [Theory]
    [InlineData(nameof(Fixtures.JpegProgressive))]
    [InlineData(nameof(Fixtures.JpegProgressive420))]
    [InlineData(nameof(Fixtures.JpegProgressive444))]
    [InlineData(nameof(Fixtures.JpegProgressiveGray))]
    [InlineData(nameof(Fixtures.JpegProgressiveOptimized))]
    public void Progressive_Jpeg_Decodes_Within_Rounding_Of_The_Reference(string name)
    {
        var fixture = Fixtures.Get(name);
        var image = JpegDecoder.Decode(fixture.File, 1_000_000, out _);
        Assert.Equal((fixture.Width, fixture.Height), (image.Width, image.Height));
        Assert.True(TestImages.MeanAbsoluteDifference(fixture.ExpectedRgba, image.Pixels) < 0.5);
        Assert.All(fixture.ExpectedRgba.Zip(image.Pixels), pair => Assert.InRange(Math.Abs(pair.First - pair.Second), 0, 4));
    }

    [Theory]
    [InlineData(nameof(Fixtures.GifInterlaced))]
    [InlineData(nameof(Fixtures.GifProgressiveRows))]
    [InlineData(nameof(Fixtures.GifTransparent))]
    public void Gif_Decodes_Exactly_Like_The_Reference(string name)
    {
        var fixture = Fixtures.Get(name);
        var image = GifDecoder.Decode(fixture.File, 1_000_000);
        Assert.Equal((fixture.Width, fixture.Height), (image.Width, image.Height));
        AssertSamePixels(fixture.ExpectedRgba, image.Pixels);
    }

    [Fact]
    public void Gif_Transparent_Colour_Index_Becomes_Alpha()
    {
        var image = GifDecoder.Decode(Fixtures.GifTransparent.File, 1_000_000);
        Assert.True(image.HasTransparency());
    }

    [Fact]
    public async Task Animated_Gif_Is_Rejected_Rather_Than_Flattened_To_A_Still()
    {
        var fixture = Fixtures.GifAnimated;
        var ex = Assert.Throws<NotSupportedException>(() => GifDecoder.Decode(fixture.File, 1_000_000));
        Assert.Contains("Animated", ex.Message);

        // small enough: returned untouched, animation intact
        var small = await ImageCompressor.CompressAsync(new MemoryStream(fixture.File));
        Assert.False(small.WasCompressed);
        Assert.Equal(fixture.File, ((MemoryStream)small.Content).ToArray());

        await Assert.ThrowsAsync<UnsupportedImageException>(() => ImageCompressor.CompressAsync(new MemoryStream(fixture.File), new ImageCompressionOptions { TargetSizeBytes = 1 }));
    }

    [Theory]
    [InlineData(nameof(Fixtures.Bmp24))]
    [InlineData(nameof(Fixtures.Bmp8))]
    [InlineData(nameof(Fixtures.Bmp1))]
    [InlineData(nameof(Fixtures.BmpGray8))]
    [InlineData(nameof(Fixtures.Bmp4))]
    [InlineData(nameof(Fixtures.Bmp8TopDown))]
    [InlineData(nameof(Fixtures.BmpRle8))]
    [InlineData(nameof(Fixtures.BmpRle4))]
    [InlineData(nameof(Fixtures.Bmp565))]
    [InlineData(nameof(Fixtures.Bmp32V4Alpha))]
    public void Bmp_Decodes_Exactly(string name)
    {
        var fixture = Fixtures.Get(name);
        var image = BmpDecoder.Decode(fixture.File, 1_000_000);
        Assert.Equal((fixture.Width, fixture.Height), (image.Width, image.Height));
        Assert.Equal(fixture.ExpectedRgba, image.Pixels);
    }

    [Fact]
    public void Bmp_32_Bit_Without_Masks_Treats_The_Unused_Byte_As_Opaque()
    {
        var fixture = Fixtures.Bmp32Alpha;
        var image = BmpDecoder.Decode(fixture.File, 1_000_000);
        Assert.Equal((fixture.Width, fixture.Height), (image.Width, image.Height));
        for (var i = 0; i < image.Pixels.Length; i += 4)
        {
            Assert.Equal(255, image.Pixels[i + 3]);
            Assert.Equal(fixture.ExpectedRgba[i], image.Pixels[i]);
            Assert.Equal(fixture.ExpectedRgba[i + 1], image.Pixels[i + 1]);
            Assert.Equal(fixture.ExpectedRgba[i + 2], image.Pixels[i + 2]);
        }
    }

    [Theory]
    [InlineData(nameof(Fixtures.JpegProgressive420), "image/jpeg")]
    [InlineData(nameof(Fixtures.GifInterlaced), "image/jpeg")]
    [InlineData(nameof(Fixtures.GifTransparent), "image/png")]
    [InlineData(nameof(Fixtures.Bmp24), "image/jpeg")]
    [InlineData(nameof(Fixtures.BmpRle8), "image/jpeg")]
    [InlineData(nameof(Fixtures.Bmp32V4Alpha), "image/png")]
    public async Task Every_Supported_Format_Compresses_End_To_End(string name, string expectedContentType)
    {
        var fixture = Fixtures.Get(name);
        var result = await ImageCompressor.CompressAsync(new MemoryStream(fixture.File), new ImageCompressionOptions { TargetSizeBytes = 1 });
        Assert.True(result.WasCompressed);
        Assert.Equal(expectedContentType, result.ContentType);
        Assert.Equal(expectedContentType == "image/png" ? ImageFormat.Png : ImageFormat.Jpeg, await ImageCompressor.DetectFormatAsync(result.Content));
    }

    [Fact]
    public async Task Unsupported_Jpeg_Codings_Still_Fail_Clearly()
    {
        // an arithmetic-coded frame header (SOF9), not decodable here
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xC9, 0x00, 0x0B, 8, 0, 4, 0, 4, 1, 1, 0x11, 0, 0xFF, 0xD9];
        await Assert.ThrowsAsync<UnsupportedImageException>(() => ImageCompressor.CompressAsync(new MemoryStream(jpeg), new ImageCompressionOptions { TargetSizeBytes = 1 }));
    }

    /// <summary>Same RGBA, except that a fully transparent pixel's colour is irrelevant.</summary>
    private static void AssertSamePixels(byte[] expected, byte[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (var i = 0; i < expected.Length; i += 4)
        {
            Assert.Equal(expected[i + 3], actual[i + 3]);
            if (expected[i + 3] == 0) continue;
            Assert.Equal(expected[i], actual[i]);
            Assert.Equal(expected[i + 1], actual[i + 1]);
            Assert.Equal(expected[i + 2], actual[i + 2]);
        }
    }
}
