namespace YandexGamesAnalytics.Services;

public sealed class AnalyticsCache
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AnalyticsSnapshot? _snapshot;
    private long _generation;
    private long _builtFor = -1;

    public void Invalidate() => Interlocked.Increment(ref _generation);

    public async Task<AnalyticsSnapshot> GetOrBuildAsync(
        Func<CancellationToken, Task<AnalyticsSnapshot>> build, CancellationToken ct)
    {
        var generation = Volatile.Read(ref _generation);
        var cached = _snapshot;
        if (cached is not null && _builtFor == generation)
            return cached;

        await _gate.WaitAsync(ct);
        try
        {
            generation = Volatile.Read(ref _generation);
            if (_snapshot is not null && _builtFor == generation)
                return _snapshot;

            var built = await build(ct);
            _snapshot = built;
            _builtFor = generation;
            return built;
        }
        finally
        {
            _gate.Release();
        }
    }
}
