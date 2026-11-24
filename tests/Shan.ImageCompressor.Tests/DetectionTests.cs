namespace Shan.ImageCompressor.Tests;

public class DetectionTests
{
    [Theory]
    [InlineData(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 0 }, ImageFormat.Png)]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, ImageFormat.Jpeg)]
    [InlineData(new byte[] { (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a', 0 }, ImageFormat.Gif)]
    [InlineData(new byte[] { 1, 2, 3 }, ImageFormat.Unknown)]
    [InlineData(new byte[0], ImageFormat.Unknown)]
    public void Detects_From_The_Bytes(byte[] bytes, ImageFormat expected) =>
        Assert.Equal(expected, ImageCompressor.DetectFormat(bytes));

    [Fact]
    public void Detects_WebP_And_Bmp()
    {
        var webp = "RIFF\0\0\0\0WEBPVP8 "u8.ToArray();
        var bmp = new byte[20];
        bmp[0] = (byte)'B'; bmp[1] = (byte)'M';
        Assert.Equal(ImageFormat.WebP, ImageCompressor.DetectFormat(webp));
        Assert.Equal(ImageFormat.Bmp, ImageCompressor.DetectFormat(bmp));
    }

    [Fact]
    public async Task Stream_Detection_Resets_The_Position()
    {
        using var stream = new MemoryStream([0xFF, 0xD8, 0xFF, 0xE0, 0, 0]);
        stream.Position = 3;
        Assert.Equal(ImageFormat.Jpeg, await ImageCompressor.DetectFormatAsync(stream));
        Assert.Equal(0, stream.Position);
    }
}
