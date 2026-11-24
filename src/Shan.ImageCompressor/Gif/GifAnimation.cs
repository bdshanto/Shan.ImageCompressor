namespace Shan.ImageCompressor.Gif;

/// <summary>One step of an animation: the whole screen as it looks while this frame is shown, and how long that lasts (hundredths of a second).</summary>
internal sealed record GifFrame(RgbaImage Image, int DelayCentiseconds);

/// <summary>A decoded animated GIF. <paramref name="Loop"/> is the NETSCAPE repeat count (0 = forever), or -1 when the file has none and plays once.</summary>
internal sealed record GifAnimation(int Width, int Height, IReadOnlyList<GifFrame> Frames, int Loop);
