namespace Shan.ImageCompressor;

/// <summary>
/// Spreads independent row/block work over cores for the duration of one compression call. The degree comes from
/// <see cref="ImageCompressionOptions.MaxDegreeOfParallelism"/>; small jobs run inline, where thread hand-off would cost more than it saves.
/// </summary>
internal static class Work
{
    private const int MinimumParallelRows = 32;

    [ThreadStatic] private static int _degree;

    /// <summary>Sets the degree of parallelism for the current thread until the returned scope is disposed.</summary>
    public static Scope Use(int degree)
    {
        var previous = _degree;
        _degree = degree;
        return new Scope(previous);
    }

    public readonly struct Scope(int previous) : IDisposable
    {
        public void Dispose() => _degree = previous;
    }

    /// <summary>Runs <paramref name="body"/> for every row index in 0..<paramref name="rows"/>, in parallel when worthwhile. Rows must be independent.</summary>
    public static void Rows(int rows, Action<int> body)
    {
        var degree = _degree == 0 ? Environment.ProcessorCount : _degree;
        if (degree <= 1 || rows < MinimumParallelRows)
        {
            for (var i = 0; i < rows; i++) body(i);
            return;
        }
        Parallel.For(0, rows, new ParallelOptions { MaxDegreeOfParallelism = degree }, body);
    }

    /// <summary>Like <see cref="Rows"/>, with a per-worker scratch value created once per worker rather than once per row.</summary>
    public static void Rows<TLocal>(int rows, Func<TLocal> create, Action<int, TLocal> body)
    {
        var degree = _degree == 0 ? Environment.ProcessorCount : _degree;
        if (degree <= 1 || rows < MinimumParallelRows)
        {
            var local = create();
            for (var i = 0; i < rows; i++) body(i, local);
            return;
        }
        Parallel.For(0, rows, new ParallelOptions { MaxDegreeOfParallelism = degree }, create, (i, _, local) => { body(i, local); return local; }, _ => { });
    }
}
