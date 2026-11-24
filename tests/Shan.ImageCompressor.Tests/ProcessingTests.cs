using Shan.ImageCompressor.Processing;

namespace Shan.ImageCompressor.Tests;

public class ProcessingTests
{
    private static RgbaImage Numbered(int width, int height)
    {
        var image = RgbaImage.Create(width, height);
        for (var i = 0; i < width * height; i++) image.Pixels[i * 4] = (byte)i; // red channel = row-major index
        return image;
    }

    private static int[] Reds(RgbaImage image) => Enumerable.Range(0, image.Width * image.Height).Select(i => (int)image.Pixels[i * 4]).ToArray();

    // 3 wide × 2 tall:  0 1 2
    //                   3 4 5
    [Theory]
    [InlineData(1, 3, 2, new[] { 0, 1, 2, 3, 4, 5 })]
    [InlineData(2, 3, 2, new[] { 2, 1, 0, 5, 4, 3 })]
    [InlineData(3, 3, 2, new[] { 5, 4, 3, 2, 1, 0 })]
    [InlineData(4, 3, 2, new[] { 3, 4, 5, 0, 1, 2 })]
    [InlineData(5, 2, 3, new[] { 0, 3, 1, 4, 2, 5 })]
    [InlineData(6, 2, 3, new[] { 3, 0, 4, 1, 5, 2 })]
    [InlineData(7, 2, 3, new[] { 5, 2, 4, 1, 3, 0 })]
    [InlineData(8, 2, 3, new[] { 2, 5, 1, 4, 0, 3 })]
    public void Orientation_Matches_The_Exif_Definition(int orientation, int width, int height, int[] expected)
    {
        var result = Orienter.Apply(Numbered(3, 2), orientation);
        Assert.Equal((width, height), (result.Width, result.Height));
        Assert.Equal(expected, Reds(result));
    }

    [Fact]
    public void Shrink_Averages_Areas()
    {
        var source = RgbaImage.Create(4, 2);
        for (var i = 0; i < 8; i++)
        {
            source.Pixels[i * 4] = (byte)(i % 4 < 2 ? 0 : 200);
            source.Pixels[i * 4 + 3] = 255;
        }
        var result = Resizer.Shrink(source, 2, 1);
        Assert.Equal((2, 1), (result.Width, result.Height));
        Assert.Equal(0, result.Pixels[0]);
        Assert.Equal(200, result.Pixels[4]);
    }

    [Fact]
    public void Shrink_Does_Not_Bleed_Colour_From_Transparent_Pixels()
    {
        var source = RgbaImage.Create(2, 2);
        source.Pixels[0] = 255; source.Pixels[3] = 255; // one opaque red pixel, three fully transparent black ones
        var result = Resizer.Shrink(source, 1, 1);
        Assert.Equal(255, result.Pixels[0]);
        Assert.Equal(0, result.Pixels[1]);
        Assert.Equal(64, result.Pixels[3]);
    }

    [Fact]
    public void Shrink_Never_Enlarges()
    {
        var source = Numbered(4, 4);
        Assert.Same(source, Resizer.Shrink(source, 10, 10));
    }
}
