namespace Shan.ImageCompressor.Tests;

/// <summary>Hostile and sloppy input: whatever the bytes, the only acceptable outcomes are a result, InvalidDataException or NotSupportedException.</summary>
public class RobustnessTests
{
    private static readonly string[] AllFixtures = typeof(Fixtures).GetFields()
        .Where(f => f.FieldType == typeof(Fixture)).Select(f => f.Name).ToArray();

    public static TheoryData<string> FixtureNames => new(AllFixtures);

    private static readonly ImageCompressionOptions Strict = new() { TargetSizeBytes = 1, MaxPixels = 250_000 };

    [Theory]
    [MemberData(nameof(FixtureNames))]
    public void Random_Corruption_Never_Escapes_As_Another_Exception(string name)
    {
        var original = Fixtures.Get(name).File;
        var random = new Random(name.GetHashCode() ^ 12345);
        for (var trial = 0; trial < 150; trial++)
        {
            var bytes = original.ToArray();
            var edits = random.Next(1, 6);
            for (var i = 0; i < edits; i++) bytes[random.Next(bytes.Length)] = (byte)random.Next(256);
            AssertCleanOutcome(bytes, name, trial);
        }
    }

    [Theory]
    [MemberData(nameof(FixtureNames))]
    public void Truncation_Never_Escapes_As_Another_Exception(string name)
    {
        var original = Fixtures.Get(name).File;
        var step = Math.Max(1, original.Length / 60);
        for (var length = 0; length < original.Length; length += step)
            AssertCleanOutcome(original[..length], name, length);
    }

    [Theory]
    [MemberData(nameof(FixtureNames))]
    public void An_Oversized_Image_Is_Refused_From_The_Header(string name)
    {
        var fixture = Fixtures.Get(name);
        var limit = (long)fixture.Width * fixture.Height - 1;
        var ex = Assert.Throws<InvalidDataException>(() => ImageCompressor.Compress(fixture.File, new ImageCompressionOptions { TargetSizeBytes = 1, MaxPixels = limit }));
        Assert.Contains("limit", ex.Message);
    }

    [Fact]
    public void A_Huge_Declared_Size_Is_Refused_Without_Allocating()
    {
        // BMP header declaring 100000 x 100000 pixels
        var bmp = new byte[64];
        bmp[0] = (byte)'B'; bmp[1] = (byte)'M';
        BitConverter.GetBytes(54).CopyTo(bmp, 10);
        BitConverter.GetBytes(40).CopyTo(bmp, 14);
        BitConverter.GetBytes(100_000).CopyTo(bmp, 18);
        BitConverter.GetBytes(100_000).CopyTo(bmp, 22);
        BitConverter.GetBytes((short)1).CopyTo(bmp, 26);
        BitConverter.GetBytes((short)24).CopyTo(bmp, 28);
        Assert.Throws<InvalidDataException>(() => ImageCompressor.Compress(bmp, new ImageCompressionOptions { TargetSizeBytes = 1 }));
    }

    [Fact]
    public void Not_An_Image_Is_Invalid_Data()
    {
        Assert.Throws<InvalidDataException>(() => ImageCompressor.Compress("this is plain text, not a picture"u8.ToArray()));
        Assert.Throws<InvalidDataException>(() => ImageCompressor.Compress([]));
    }

    private static void AssertCleanOutcome(byte[] bytes, string name, int trial)
    {
        try
        {
            using var result = ImageCompressor.Compress(bytes, Strict).Content;
        }
        catch (Exception e) when (e is InvalidDataException or NotSupportedException) { }
        catch (Exception e)
        {
            throw new Xunit.Sdk.XunitException($"{name} (trial {trial}) threw {e.GetType().Name}: {e.Message}\n{e.StackTrace}");
        }
    }
}
