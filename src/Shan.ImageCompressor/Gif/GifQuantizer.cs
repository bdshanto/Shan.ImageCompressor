namespace Shan.ImageCompressor.Gif;

/// <summary>Picks a palette for a set of frames by median cut over a 5-5-5 colour histogram, and maps pixels onto it.</summary>
internal sealed class GifQuantizer
{
    private const int NearestBits = 6; // colours are matched at 6 bits per channel, cached per cell

    private readonly byte[] _palette; // RGB triples
    private readonly int _count;
    private readonly int[] _cache = new int[1 << (NearestBits * 3)];

    private GifQuantizer(byte[] palette)
    {
        _palette = palette;
        _count = palette.Length / 3;
        Array.Fill(_cache, -1);
    }

    /// <summary>The palette, as RGB triples.</summary>
    public byte[] Palette => _palette;

    /// <summary>Builds a palette of at most <paramref name="colours"/> entries from the opaque pixels (alpha ≥ 128) of every frame.</summary>
    public static GifQuantizer Build(IReadOnlyList<RgbaImage> frames, int colours)
    {
        var count = new long[1 << 15];
        var sum = new long[3][] { new long[1 << 15], new long[1 << 15], new long[1 << 15] };
        foreach (var frame in frames)
        {
            var p = frame.Pixels;
            for (var i = 0; i < p.Length; i += 4)
            {
                if (p[i + 3] < 128) continue;
                int r = p[i], g = p[i + 1], b = p[i + 2];
                var key = (r >> 3) << 10 | (g >> 3) << 5 | b >> 3;
                count[key]++;
                sum[0][key] += r;
                sum[1][key] += g;
                sum[2][key] += b;
            }
        }

        var bins = new List<int>();
        for (var k = 0; k < count.Length; k++)
            if (count[k] > 0) bins.Add(k);
        if (bins.Count == 0) return new GifQuantizer([0, 0, 0]);

        var order = bins.ToArray();
        var boxes = new List<(int Start, int End, long Total)> { (0, order.Length, order.Sum(k => count[k])) };

        while (boxes.Count < colours)
        {
            var pick = -1;
            for (var i = 0; i < boxes.Count; i++)
                if (boxes[i].End - boxes[i].Start > 1 && (pick < 0 || boxes[i].Total > boxes[pick].Total)) pick = i;
            if (pick < 0) break;

            var (start, end, total) = boxes[pick];
            int[] low = [31, 31, 31], high = [0, 0, 0];
            for (var i = start; i < end; i++)
                for (var c = 0; c < 3; c++)
                {
                    var v = Component(order[i], c);
                    if (v < low[c]) low[c] = v;
                    if (v > high[c]) high[c] = v;
                }
            var channel = 0;
            for (var c = 1; c < 3; c++)
                if (high[c] - low[c] > high[channel] - low[channel]) channel = c;

            var ch = channel;
            Array.Sort(order, start, end - start, Comparer<int>.Create((a, b) => Component(a, ch).CompareTo(Component(b, ch))));

            long running = 0;
            var split = start + 1;
            for (var i = start; i < end - 1; i++)
            {
                running += count[order[i]];
                split = i + 1;
                if (running * 2 >= total) break;
            }
            boxes[pick] = (start, split, order.Skip(start).Take(split - start).Sum(k => count[k]));
            boxes.Add((split, end, total - boxes[pick].Total));
        }

        var palette = new byte[boxes.Count * 3];
        for (var b = 0; b < boxes.Count; b++)
        {
            long n = 0, r = 0, g = 0, bl = 0;
            for (var i = boxes[b].Start; i < boxes[b].End; i++)
            {
                var k = order[i];
                n += count[k]; r += sum[0][k]; g += sum[1][k]; bl += sum[2][k];
            }
            palette[b * 3] = (byte)((r + n / 2) / n);
            palette[b * 3 + 1] = (byte)((g + n / 2) / n);
            palette[b * 3 + 2] = (byte)((bl + n / 2) / n);
        }
        return new GifQuantizer(palette);
    }

    private static int Component(int key, int channel) => channel switch { 0 => key >> 10, 1 => (key >> 5) & 31, _ => key & 31 };

    /// <summary>The index of the palette entry closest to the colour.</summary>
    public int Nearest(int r, int g, int b)
    {
        var key = (r >> (8 - NearestBits)) << (NearestBits * 2) | (g >> (8 - NearestBits)) << NearestBits | b >> (8 - NearestBits);
        var cached = _cache[key];
        if (cached >= 0) return cached;

        const int Half = 1 << (7 - NearestBits); // the middle of the cell
        int cr = (r >> (8 - NearestBits) << (8 - NearestBits)) + Half, cg = (g >> (8 - NearestBits) << (8 - NearestBits)) + Half, cb = (b >> (8 - NearestBits) << (8 - NearestBits)) + Half;
        var best = 0;
        var bestDistance = int.MaxValue;
        for (var i = 0; i < _count; i++)
        {
            int dr = _palette[i * 3] - cr, dg = _palette[i * 3 + 1] - cg, db = _palette[i * 3 + 2] - cb;
            var distance = dr * dr * 2 + dg * dg * 4 + db * db * 3; // weighted towards how the eye sees green
            if (distance < bestDistance) { bestDistance = distance; best = i; }
        }
        return _cache[key] = best;
    }
}
