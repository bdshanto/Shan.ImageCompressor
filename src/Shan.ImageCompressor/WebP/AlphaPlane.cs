namespace Shan.ImageCompressor.WebP;

/// <summary>The "ALPH" chunk of a lossy WebP: a transparency plane, stored raw or losslessly compressed, optionally predicted from its neighbours.</summary>
internal static class AlphaPlane
{
    public static void Apply(ReadOnlySpan<byte> chunk, RgbaImage image)
    {
        if (chunk.Length < 1) throw new InvalidDataException("The WebP alpha chunk is empty.");
        var compression = chunk[0] & 3;
        var filter = (chunk[0] >> 2) & 3;
        var body = chunk[1..];
        int width = image.Width, height = image.Height;

        byte[] alpha;
        switch (compression)
        {
            case 0:
                if (body.Length < (long)width * height) throw new InvalidDataException("The WebP alpha plane is truncated.");
                alpha = body[..(width * height)].ToArray();
                break;
            case 1:
                alpha = Vp8LDecoder.DecodeAlpha(body, width, height);
                break;
            default:
                throw new InvalidDataException("The WebP alpha plane uses an unknown compression method.");
        }

        if (filter != 0) Unfilter(alpha, width, height, filter);
        for (var i = 0; i < alpha.Length; i++) image.Pixels[i * 4 + 3] = alpha[i];
    }

    /// <summary>Undoes the encoder's prediction: each stored byte is the difference from a horizontal, vertical or gradient guess.</summary>
    private static void Unfilter(byte[] a, int width, int height, int filter)
    {
        for (var y = 0; y < height; y++)
        {
            var row = y * width;
            if (y == 0 || filter == 1)
            {
                // left prediction; a row after the first starts from the pixel above it
                var predictor = y == 0 ? 0 : a[row - width];
                for (var x = 0; x < width; x++)
                {
                    a[row + x] = (byte)(a[row + x] + predictor);
                    predictor = a[row + x];
                }
            }
            else if (filter == 2)
            {
                for (var x = 0; x < width; x++) a[row + x] = (byte)(a[row + x] + a[row - width + x]);
            }
            else
            {
                int top = a[row - width], topLeft = top, left = top;
                for (var x = 0; x < width; x++)
                {
                    top = a[row - width + x];
                    var guess = left + top - topLeft;
                    left = (byte)(a[row + x] + (guess < 0 ? 0 : guess > 255 ? 255 : guess));
                    topLeft = top;
                    a[row + x] = (byte)left;
                }
            }
        }
    }
}
