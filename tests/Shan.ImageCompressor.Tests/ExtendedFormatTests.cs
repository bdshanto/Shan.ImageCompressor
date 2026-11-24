using Shan.ImageCompressor.Color;
using Shan.ImageCompressor.Jpeg;
using Shan.ImageCompressor.Pnm;
using Shan.ImageCompressor.Png;
using Shan.ImageCompressor.Qoi;
using Shan.ImageCompressor.Tga;
using Shan.ImageCompressor.Tiff;

namespace Shan.ImageCompressor.Tests;

/// <summary>TIFF, TGA, PNM, QOI and CMYK JPEG decoding, ICC → sRGB conversion, metadata carry-over, output choice and the typed exceptions.</summary>
public class ExtendedFormatTests
{
    private const long Limit = 1_000_000;

    [Theory]
    [InlineData(nameof(Fixtures.TiffRgb))]
    [InlineData(nameof(Fixtures.TiffLzw))]
    [InlineData(nameof(Fixtures.TiffDeflate))]
    [InlineData(nameof(Fixtures.TiffPackBits))]
    [InlineData(nameof(Fixtures.TiffPalette))]
    [InlineData(nameof(Fixtures.TiffGray))]
    [InlineData(nameof(Fixtures.TiffBilevel))]
    [InlineData(nameof(Fixtures.TiffRgba))]
    [InlineData(nameof(Fixtures.TiffBigEndianStrips))]
    [InlineData(nameof(Fixtures.TiffPredictor))]
    public void Tiff_Decodes_Exactly(string name)
    {
        var fixture = Fixtures.Get(name);
        var image = TiffDecoder.Decode(fixture.File, Limit, out var orientation);
        Assert.Equal(1, orientation);
        Assert.Equal((fixture.Width, fixture.Height), (image.Width, image.Height));
        AssertSamePixels(fixture.ExpectedRgba, image.Pixels);
    }

    [Fact]
    public void Tiff_Cmyk_Matches_The_Reference_Conversion()
    {
        var fixture = Fixtures.TiffCmyk;
        var image = TiffDecoder.Decode(fixture.File, Limit, out _);
        AssertWithin(fixture.ExpectedRgba, image.Pixels, 2);
    }

    [Theory]
    [InlineData(nameof(Fixtures.TgaRgb))]
    [InlineData(nameof(Fixtures.TgaRgbRle))]
    [InlineData(nameof(Fixtures.TgaRgbaRle))]
    [InlineData(nameof(Fixtures.TgaGray))]
    [InlineData(nameof(Fixtures.TgaPalette))]
    public void Tga_Decodes_Exactly(string name)
    {
        var fixture = Fixtures.Get(name);
        Assert.True(TgaDecoder.LooksLikeTga(fixture.File, fixture.File.Length));
        var image = TgaDecoder.Decode(fixture.File, Limit);
        Assert.Equal((fixture.Width, fixture.Height), (image.Width, image.Height));
        AssertSamePixels(fixture.ExpectedRgba, image.Pixels);
    }

    [Theory]
    [InlineData(nameof(Fixtures.PnmPpm))]
    [InlineData(nameof(Fixtures.PnmPgm))]
    [InlineData(nameof(Fixtures.PnmPbm))]
    [InlineData(nameof(Fixtures.PnmAsciiRgb))]
    [InlineData(nameof(Fixtures.PnmAsciiGray16))]
    [InlineData(nameof(Fixtures.PnmAsciiBitmap))]
    [InlineData(nameof(Fixtures.PnmBinaryGray16))]
    public void Pnm_Decodes_Exactly(string name)
    {
        var fixture = Fixtures.Get(name);
        var image = PnmDecoder.Decode(fixture.File, Limit);
        Assert.Equal((fixture.Width, fixture.Height), (image.Width, image.Height));
        Assert.Equal(fixture.ExpectedRgba, image.Pixels);
    }

    [Fact]
    public void Qoi_Decodes_Exactly()
    {
        var fixture = Fixtures.Qoi;
        var image = QoiDecoder.Decode(fixture.File, Limit);
        Assert.Equal((fixture.Width, fixture.Height), (image.Width, image.Height));
        Assert.Equal(fixture.ExpectedRgba, image.Pixels);
    }

    [Fact]
    public void Cmyk_Jpeg_Matches_The_Reference_Conversion()
    {
        var fixture = Fixtures.JpegCmyk;
        var image = JpegDecoder.Decode(fixture.File, Limit, out _);
        Assert.Equal((fixture.Width, fixture.Height), (image.Width, image.Height));
        Assert.True(TestImages.MeanAbsoluteDifference(fixture.ExpectedRgba, image.Pixels, ignoreAlpha: true) < 1.5);
    }

    [Theory]
    [InlineData(nameof(Fixtures.TiffLzw), ImageFormat.Tiff)]
    [InlineData(nameof(Fixtures.TgaRgbRle), ImageFormat.Tga)]
    [InlineData(nameof(Fixtures.PnmPpm), ImageFormat.Pnm)]
    [InlineData(nameof(Fixtures.PnmAsciiRgb), ImageFormat.Pnm)]
    [InlineData(nameof(Fixtures.Qoi), ImageFormat.Qoi)]
    public async Task Formats_Are_Detected_From_The_Bytes(string name, ImageFormat expected)
    {
        var file = Fixtures.Get(name).File;
        Assert.Equal(expected, ImageCompressor.DetectFormat(file));
        Assert.Equal(expected, await ImageCompressor.DetectFormatAsync(new MemoryStream(file)));
    }

    [Theory]
    [InlineData(nameof(Fixtures.TiffDeflate), "image/jpeg")]
    [InlineData(nameof(Fixtures.TiffRgba), "image/png")]
    [InlineData(nameof(Fixtures.TgaRgbRle), "image/jpeg")]
    [InlineData(nameof(Fixtures.TgaRgbaRle), "image/png")]
    [InlineData(nameof(Fixtures.PnmPpm), "image/jpeg")]
    [InlineData(nameof(Fixtures.Qoi), "image/png")] // the last rows have real alpha
    [InlineData(nameof(Fixtures.JpegCmyk), "image/jpeg")]
    public async Task New_Formats_Compress_End_To_End(string name, string expectedContentType)
    {
        var result = await ImageCompressor.CompressAsync(new MemoryStream(Fixtures.Get(name).File), new ImageCompressionOptions { TargetSizeBytes = 1 });
        Assert.True(result.WasCompressed);
        Assert.Equal(expectedContentType, result.ContentType);
    }

    // ---------------------------------------------------------------- colour management

    [Fact]
    public void Embedded_Matrix_Profile_Converts_To_Srgb_Like_LittleCms()
    {
        var png = Fixtures.PngAdobeRgb;
        var decoded = PngDecoder.Decode(png.File, Limit);
        var converter = SrgbConverter.TryCreate(decoded.Metadata!.IccProfile!);
        Assert.NotNull(converter);

        converter.Apply(decoded.Pixels);
        AssertWithin(png.ExpectedRgba, decoded.Pixels, 3);
    }

    [Fact]
    public void Jpeg_With_A_Profile_Converts_To_Srgb_Within_Rounding()
    {
        var fixture = Fixtures.JpegAdobeRgb;
        var decoded = JpegDecoder.Decode(fixture.File, Limit, out _);
        SrgbConverter.TryCreate(decoded.Metadata!.IccProfile!)!.Apply(decoded.Pixels);
        AssertWithin(fixture.ExpectedRgba, decoded.Pixels, 5);
    }

    [Fact]
    public void An_Srgb_Profile_Needs_No_Conversion()
    {
        var decoded = PngDecoder.Decode(Fixtures.PngSrgbProfile.File, Limit);
        Assert.NotNull(decoded.Metadata?.IccProfile);
        Assert.Null(SrgbConverter.TryCreate(decoded.Metadata!.IccProfile!));
    }

    [Fact]
    public void A_Garbage_Profile_Is_Ignored_Not_Fatal()
    {
        Assert.Null(SrgbConverter.TryCreate([1, 2, 3]));
        Assert.Null(SrgbConverter.TryCreate(new byte[200]));
    }

    [Fact]
    public async Task Compression_Converts_A_Wide_Gamut_Image_To_Srgb_By_Default()
    {
        var fixture = Fixtures.PngAdobeRgb;
        var result = await ImageCompressor.CompressAsync(new MemoryStream(fixture.File), new ImageCompressionOptions { TargetSizeBytes = 1, InitialQuality = 100, MinQuality = 100, Output = OutputFormat.Png });
        var output = PngDecoder.Decode(((MemoryStream)result.Content).ToArray(), Limit);
        Assert.Null(output.Metadata);
        AssertWithin(fixture.ExpectedRgba, output.Pixels, 3);
    }

    // ---------------------------------------------------------------- metadata

    [Fact]
    public async Task Metadata_Is_Dropped_By_Default()
    {
        var result = await ImageCompressor.CompressAsync(new MemoryStream(Fixtures.JpegExifIcc.File), new ImageCompressionOptions { TargetSizeBytes = 1 });
        var output = JpegDecoder.Decode(((MemoryStream)result.Content).ToArray(), Limit, out _);
        Assert.Null(output.Metadata);
    }

    [Fact]
    public async Task Kept_Exif_Is_Carried_With_Its_Orientation_Reset()
    {
        var result = await ImageCompressor.CompressAsync(new MemoryStream(Fixtures.JpegExifIcc.File), new ImageCompressionOptions { TargetSizeBytes = 1, KeepMetadata = true });
        var bytes = ((MemoryStream)result.Content).ToArray();
        var output = JpegDecoder.Decode(bytes, Limit, out var orientation);

        Assert.Equal(1, orientation);
        Assert.Equal((24, 40), (output.Width, output.Height)); // the source said "rotate 90°", and the pixels were rotated
        var exif = output.Metadata!.Exif!;
        Assert.Contains("TestMaker", System.Text.Encoding.ASCII.GetString(exif));
        Assert.Equal(1, ExifOrientation.Read([.. "Exif\0\0"u8.ToArray(), .. exif]));
        Assert.Null(output.Metadata.IccProfile); // converted to sRGB, so the original profile no longer describes the pixels
    }

    [Fact]
    public async Task Without_Conversion_The_Icc_Profile_Is_Kept_Intact()
    {
        var source = JpegDecoder.Decode(Fixtures.JpegExifIcc.File, Limit, out _).Metadata!.IccProfile!;
        var result = await ImageCompressor.CompressAsync(new MemoryStream(Fixtures.JpegExifIcc.File),
            new ImageCompressionOptions { TargetSizeBytes = 1, KeepMetadata = true, ConvertToSrgb = false });
        var output = JpegDecoder.Decode(((MemoryStream)result.Content).ToArray(), Limit, out _);
        Assert.Equal(source, output.Metadata!.IccProfile);
    }

    [Fact]
    public async Task Png_Output_Carries_Exif_Too()
    {
        var result = await ImageCompressor.CompressAsync(new MemoryStream(Fixtures.PngExif.File),
            new ImageCompressionOptions { TargetSizeBytes = 1, KeepMetadata = true, Output = OutputFormat.Png });
        var output = PngDecoder.Decode(((MemoryStream)result.Content).ToArray(), Limit);
        Assert.Contains("TestMaker", System.Text.Encoding.ASCII.GetString(output.Metadata!.Exif!));
    }

    // ---------------------------------------------------------------- output choice

    [Fact]
    public async Task Output_Png_Forces_Png_For_An_Opaque_Image()
    {
        var result = await ImageCompressor.CompressAsync(new MemoryStream(Fixtures.TiffRgb.File), new ImageCompressionOptions { TargetSizeBytes = 1, Output = OutputFormat.Png });
        Assert.Equal("image/png", result.ContentType);
    }

    [Fact]
    public async Task Output_Jpeg_Flattens_Transparency_Onto_White()
    {
        var result = await ImageCompressor.CompressAsync(new MemoryStream(Fixtures.GifTransparent.File), new ImageCompressionOptions { TargetSizeBytes = 1, Output = OutputFormat.Jpeg });
        Assert.Equal("image/jpeg", result.ContentType);
        var decoded = JpegDecoder.Decode(((MemoryStream)result.Content).ToArray(), Limit, out _);
        Assert.False(decoded.HasTransparency());
    }

    [Fact]
    public void An_Unknown_Output_Format_Is_Rejected()
    {
        var options = new ImageCompressionOptions { Output = (OutputFormat)99 };
        Assert.Throws<ArgumentOutOfRangeException>(() => ImageCompressor.Compress(TestImages.NoisyPng(8, false).ToArray(), options));
    }

    // ---------------------------------------------------------------- exceptions

    [Fact]
    public void Corrupt_Input_Throws_InvalidImageException_With_The_Format()
    {
        var bytes = Fixtures.InterlacedRgba.File[..40];
        var ex = Assert.Throws<InvalidImageException>(() => ImageCompressor.Compress(bytes, new ImageCompressionOptions { TargetSizeBytes = 1 }));
        Assert.Equal(ImageFormat.Png, ex.Format);
        Assert.IsAssignableFrom<ImageCompressionException>(ex);
    }

    [Fact]
    public void Unsupported_Input_Throws_UnsupportedImageException_With_The_Format()
    {
        var webp = new byte[200];
        "RIFF"u8.CopyTo(webp);
        "WEBP"u8.CopyTo(webp.AsSpan(8));
        var ex = Assert.Throws<UnsupportedImageException>(() => ImageCompressor.Compress(webp, new ImageCompressionOptions { TargetSizeBytes = 1 }));
        Assert.Equal(ImageFormat.WebP, ex.Format);
    }

    [Fact]
    public void BigTiff_Is_Unsupported_Not_Corrupt()
    {
        byte[] bigTiff = [.. "II+\0"u8.ToArray(), 8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        Assert.Throws<UnsupportedImageException>(() => ImageCompressor.Compress(bigTiff, new ImageCompressionOptions { TargetSizeBytes = 1 }));
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

    private static void AssertWithin(byte[] expected, byte[] actual, int tolerance)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (var i = 0; i < expected.Length; i++)
        {
            if (i % 4 == 3) continue;
            Assert.InRange(Math.Abs(expected[i] - actual[i]), 0, tolerance);
        }
    }
}
