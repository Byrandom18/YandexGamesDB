namespace YandexGamesAnalytics.Services;

public enum SyncMode
{
    Sample,
    Full
}

public sealed class SyncProgress
{
    public bool IsRunning { get; set; }
    public string Phase { get; set; } = "Ожидание";
    public string Message { get; set; } = "Данные ещё не собирались.";
    public int Current { get; set; }
    public int Total { get; set; }
    public int GamesCollected { get; set; }
    public int GamesEnriched { get; set; }
    public string? Error { get; set; }
    public DateTime? LastFinishedAt { get; set; }
    public string? LastMode { get; set; }
    public double Percent => Total <= 0 ? 0 : Math.Clamp(100.0 * Current / Total, 0, 100);
}

public sealed class SyncCoordinator
{
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _running;

    public SyncProgress Progress { get; } = new();

    public bool TryBegin(out CancellationToken token)
    {
        lock (_gate)
        {
            if (_running is { IsCompleted: false })
            {
                token = CancellationToken.None;
                return false;
            }

            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            token = _cts.Token;
            Progress.IsRunning = true;
            Progress.Error = null;
            Progress.Current = 0;
            Progress.Total = 0;
            Progress.Phase = "Запуск";
            Progress.Message = "Подготовка…";
            return true;
        }
    }

    public void Attach(Task task)
    {
        lock (_gate)
            _running = task;
    }

    public void Cancel()
    {
        lock (_gate)
            _cts?.Cancel();
    }

    public void Complete(string? error = null)
    {
        lock (_gate)
        {
            Progress.IsRunning = false;
            Progress.LastFinishedAt = DateTime.Now;
            if (error is null)
            {
                Progress.Phase = "Готово";
                Progress.Message = $"Собрано игр: {Progress.GamesCollected}, подробно: {Progress.GamesEnriched}.";
            }
            else
            {
                Progress.Phase = "Ошибка";
                Progress.Error = error;
                Progress.Message = error;
            }
        }
    }
}
