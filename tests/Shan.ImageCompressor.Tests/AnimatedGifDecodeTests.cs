using Shan.ImageCompressor.Gif;

namespace Shan.ImageCompressor.Tests;

public class AnimatedGifDecodeTests
{
    [Theory]
    [InlineData(nameof(AnimatedFixtures.AnimOpaqueDiff))]
    [InlineData(nameof(AnimatedFixtures.AnimTransparentDispose2))]
    [InlineData(nameof(AnimatedFixtures.AnimPlayOnce))]
    public void Every_Frame_Matches_The_Composited_Reference(string name)
    {
        var fixture = AnimatedFixtures.Get(name);
        var animation = GifDecoder.DecodeAnimation(fixture.File, 10_000_000);

        Assert.Equal((fixture.Width, fixture.Height), (animation.Width, animation.Height));
        Assert.Equal(fixture.ExpectedFrames.Length, animation.Frames.Count);
        Assert.Equal(fixture.DelaysCs, animation.Frames.Select(f => f.DelayCentiseconds).ToArray());
        Assert.Equal(fixture.Loop, animation.Loop);
        for (var i = 0; i < animation.Frames.Count; i++)
            Assert.True(fixture.ExpectedFrames[i].SequenceEqual(animation.Frames[i].Image.Pixels), $"frame {i} differs from the reference");
    }

    // Pillow keeps frames a "restore to previous" disposal should discard, so this one is checked against the GIF specification instead:
    // after a frame with disposal 3 the screen goes back to how it was before that frame was drawn.
    [Fact]
    public void Disposal_3_Restores_The_Screen_To_Before_The_Frame()
    {
        var animation = GifDecoder.DecodeAnimation(AnimatedFixtures.AnimRestorePrevious.File, 10_000_000);
        byte[] At(int frame, int x, int y) => animation.Frames[frame].Image.Pixels.AsSpan((y * 40 + x) * 4, 4).ToArray();
        byte[] red = [220, 40, 40, 255];

        Assert.Equal(red, At(0, 5, 6));
        Assert.NotEqual(red, At(1, 5, 6));   // frame 0 was disposed, so none of it remains
        Assert.NotEqual(red, At(2, 9, 6));   // ...and neither does frame 1's square
        Assert.NotEqual(red, At(2, 5, 6));
    }

    [Fact]
    public void A_Still_Gif_Is_One_Frame_Identical_To_The_Still_Decoder()
    {
        var animation = GifDecoder.DecodeAnimation(Fixtures.GifTransparent.File, 1_000_000);
        Assert.Single(animation.Frames);
        Assert.Equal(GifDecoder.Decode(Fixtures.GifTransparent.File, 1_000_000).Pixels, animation.Frames[0].Image.Pixels);
    }

    [Fact]
    public void The_Total_Pixels_Across_Frames_Are_Bounded()
    {
        // 40x24 = 960 px per frame; room for fewer frames than the file holds
        var ex = Assert.Throws<InvalidDataException>(() => GifDecoder.DecodeAnimation(AnimatedFixtures.AnimOpaqueDiff.File, 2_000));
        Assert.Contains("pixels", ex.Message);
    }

    [Theory]
    [InlineData(nameof(AnimatedFixtures.AnimOpaqueDiff))]
    [InlineData(nameof(AnimatedFixtures.AnimTransparentDispose2))]
    [InlineData(nameof(AnimatedFixtures.AnimRestorePrevious))]
    public void Truncated_Or_Corrupted_Animations_Only_Ever_Raise_Our_Own_Exceptions(string name)
    {
        var file = AnimatedFixtures.Get(name).File;
        var options = new ImageCompressionOptions { TargetSizeBytes = 1, MinWidth = 4 };

        void Try(byte[] bytes)
        {
            try { ImageCompressor.Compress(bytes, options); }
            catch (ImageCompressionException) { }
        }

        for (var length = 0; length < file.Length; length += 7) Try(file[..length]);

        var random = new Random(11);
        for (var round = 0; round < 150; round++)
        {
            var copy = (byte[])file.Clone();
            for (var k = 0; k < 3; k++) copy[random.Next(copy.Length)] = (byte)random.Next(256);
            Try(copy);
        }
    }
}
