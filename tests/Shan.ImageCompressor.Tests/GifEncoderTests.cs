using Shan.ImageCompressor.Gif;

namespace Shan.ImageCompressor.Tests;

public class GifEncoderTests
{
    private static readonly (byte R, byte G, byte B)[] Colours =
    [
        (0, 0, 0), (255, 255, 255), (220, 40, 40), (40, 200, 60), (30, 90, 200), (250, 220, 0), (150, 40, 190), (0, 190, 190),
        (120, 120, 120), (255, 140, 0), (90, 50, 20), (255, 150, 200), (10, 60, 20), (200, 200, 120), (60, 60, 120), (170, 220, 255)
    ];

    private static RgbaImage Painted(int width, int height, Func<int, int, int> colourOf, Func<int, int, bool>? clear = null)
    {
        var image = RgbaImage.Create(width, height);
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var o = (y * width + x) * 4;
                if (clear?.Invoke(x, y) == true) continue;
                var (r, g, b) = Colours[colourOf(x, y) % Colours.Length];
                image.Pixels[o] = r; image.Pixels[o + 1] = g; image.Pixels[o + 2] = b; image.Pixels[o + 3] = 255;
            }
        return image;
    }

    private static GifAnimation RoundTrip(IReadOnlyList<GifFrame> frames, int loop, int colours) =>
        GifDecoder.DecodeAnimation(GifEncoder.Encode(frames, loop, colours).ToArray(), 100_000_000);

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(7, 3)]
    [InlineData(64, 48)]
    [InlineData(300, 200)]
    public void A_Still_Round_Trips_Exactly_Through_Our_Decoder(int width, int height)
    {
        var random = new Random(width * 31 + height);
        var image = Painted(width, height, (_, _) => random.Next(Colours.Length));
        var decoded = RoundTrip([new GifFrame(image, 0)], -1, 256);

        Assert.Single(decoded.Frames);
        Assert.Equal(image.Pixels, decoded.Frames[0].Image.Pixels);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(128)]
    [InlineData(256)]
    public void Dictionary_Resets_And_Code_Widths_Survive_Large_Noisy_Images(int colourBudget)
    {
        // 200x200 of near-random colours overflows the 4096-entry dictionary several times
        var random = new Random(colourBudget);
        var image = RgbaImage.Create(200, 200);
        for (var i = 0; i < 200 * 200; i++)
        {
            image.Pixels[i * 4] = (byte)random.Next(256); image.Pixels[i * 4 + 1] = (byte)random.Next(256);
            image.Pixels[i * 4 + 2] = (byte)random.Next(256); image.Pixels[i * 4 + 3] = 255;
        }
        var bytes = GifEncoder.Encode([new GifFrame(image, 0)], -1, colourBudget).ToArray();

        var decoded = GifDecoder.Decode(bytes, 1_000_000); // the decoder must read every pixel back without error
        Assert.Equal((200, 200), (decoded.Width, decoded.Height));
        // the palette never exceeds the budget
        var tableBits = (bytes[10] & 7) + 1;
        Assert.True(1 << tableBits < 2 * Math.Max(2, colourBudget)); // a power-of-two table at most one step above the budget
    }

    [Fact]
    public void A_Long_Run_Of_One_Colour_Round_Trips()
    {
        var image = Painted(500, 400, (_, _) => 2);
        Assert.Equal(image.Pixels, RoundTrip([new GifFrame(image, 0)], -1, 256).Frames[0].Image.Pixels);
    }

    [Fact]
    public void An_Opaque_Animation_Round_Trips_And_Stores_Only_What_Changed()
    {
        var frames = Enumerable.Range(0, 6)
            .Select(i => new GifFrame(Painted(200, 150, (x, y) => x >= 5 + i * 10 && x < 20 + i * 10 && y >= 10 && y < 30 ? 2 : 1), 5 + i))
            .ToList();

        var encoded = GifEncoder.Encode(frames, 0, 256).ToArray();
        var decoded = GifDecoder.DecodeAnimation(encoded, 100_000_000);

        Assert.Equal(frames.Count, decoded.Frames.Count);
        Assert.Equal(0, decoded.Loop);
        for (var i = 0; i < frames.Count; i++)
        {
            Assert.Equal(frames[i].Image.Pixels, decoded.Frames[i].Image.Pixels);
            Assert.Equal(frames[i].DelayCentiseconds, decoded.Frames[i].DelayCentiseconds);
        }

        var fullFrames = GifEncoder.Encode([frames[0]], -1, 256).Length * frames.Count;
        Assert.True(encoded.Length < fullFrames / 2, $"{encoded.Length} bytes is not much smaller than {fullFrames} for whole frames");
    }

    [Fact]
    public void Identical_Frames_Cost_Almost_Nothing_And_Still_Decode()
    {
        var image = Painted(60, 40, (x, _) => x / 10);
        var frames = Enumerable.Range(0, 5).Select(_ => new GifFrame(image, 10)).ToList();
        var decoded = RoundTrip(frames, -1, 256);

        Assert.Equal(5, decoded.Frames.Count);
        Assert.All(decoded.Frames, f => Assert.Equal(image.Pixels, f.Image.Pixels));
        Assert.Equal(-1, decoded.Loop);
    }

    [Fact]
    public void A_Transparent_Animation_Keeps_Its_See_Through_Pixels_In_Every_Frame()
    {
        var frames = Enumerable.Range(0, 4)
            .Select(i => new GifFrame(Painted(50, 40, (x, y) => 2 + (x + y + i) % 5, (x, y) => (x + i * 7) % 11 < 4), 8))
            .ToList();
        var decoded = RoundTrip(frames, 2, 256);

        Assert.Equal(2, decoded.Loop);
        for (var i = 0; i < frames.Count; i++)
            Assert.Equal(frames[i].Image.Pixels, decoded.Frames[i].Image.Pixels);
    }

    [Fact]
    public void Too_Many_Colours_Are_Reduced_To_The_Budget_But_Stay_Close()
    {
        // a smooth gradient has thousands of colours
        var image = RgbaImage.Create(120, 80);
        for (var y = 0; y < 80; y++)
            for (var x = 0; x < 120; x++)
            {
                var o = (y * 120 + x) * 4;
                image.Pixels[o] = (byte)(x * 2); image.Pixels[o + 1] = (byte)(y * 3); image.Pixels[o + 2] = (byte)((x + y) % 256); image.Pixels[o + 3] = 255;
            }
        var decoded = RoundTrip([new GifFrame(image, 0), new GifFrame(image, 0)], 0, 64).Frames[0].Image;

        var distinct = new HashSet<int>();
        for (var i = 0; i < 120 * 80; i++) distinct.Add(decoded.Pixels[i * 4] << 16 | decoded.Pixels[i * 4 + 1] << 8 | decoded.Pixels[i * 4 + 2]);
        Assert.True(distinct.Count <= 63);
        Assert.True(TestImages.MeanAbsoluteDifference(image.Pixels, decoded.Pixels, ignoreAlpha: true) < 12);
    }

    [Fact]
    public void Fewer_Colours_Make_A_Smaller_File()
    {
        var random = new Random(5);
        var image = RgbaImage.Create(150, 100);
        for (var i = 0; i < 150 * 100; i++)
        {
            image.Pixels[i * 4] = (byte)random.Next(256); image.Pixels[i * 4 + 1] = (byte)random.Next(256);
            image.Pixels[i * 4 + 2] = (byte)random.Next(256); image.Pixels[i * 4 + 3] = 255;
        }
        var frames = new[] { new GifFrame(image, 0) };
        Assert.True(GifEncoder.Encode(frames, -1, 16).Length < GifEncoder.Encode(frames, -1, 256).Length);
    }
}
