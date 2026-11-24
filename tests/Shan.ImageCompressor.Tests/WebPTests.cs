using Shan.ImageCompressor.WebP;

namespace Shan.ImageCompressor.Tests;

public class WebPTests
{
    [Fact]
    public void The_Short_Distance_Table_Has_All_120_Entries() => Assert.Equal(120, Vp8LDecoder.DistanceMapLength);

    public static IEnumerable<object[]> LosslessNames => WebPFixtures.Lossless.Select(n => new object[] { n });

    [Theory]
    [MemberData(nameof(LosslessNames))]
    public void Lossless_Decodes_Exactly_Like_libwebp(string name)
    {
        var fixture = WebPFixtures.Get(name);
        var image = WebPDecoder.Decode(fixture.File, 1_000_000, out _);

        Assert.Equal((fixture.Width, fixture.Height), (image.Width, image.Height));
        Assert.Equal(fixture.ExpectedRgba, image.Pixels);
    }
}
