namespace ToadFlow.Server.Clash.Images;

/// <summary>Кто ждёт картинку: человек (веб, плагин) или выгрузка (отчёт, архив).</summary>
public enum ImagePriority
{
    Interactive,
    Archive
}

/// <summary>Очередь интерактивных отрисовок полна: ответить 429, клиент повторит.</summary>
public sealed class RenderQueueFullException() : Exception("Картинок слишком много в очереди — повторите через несколько секунд");

/// <summary>
///     Очередь отрисовок с приоритетом и отменой. Одновременно идёт не больше <c>threads</c> отрисовок; выгрузки занимают не больше
///     <c>archiveWindow</c> из них, поэтому человек не ждёт архива. Свободный поток берёт сначала ждущую интерактивную отрисовку.
///     Одну и ту же картинку (по ключу) ждут все запросы вместе; когда ждущих не осталось, отрисовка снимается с очереди, а начатая —
///     отменяется. Ждущих интерактивных — не больше <c>interactiveCapacity</c>: дальше <see cref="RenderQueueFullException" />.
/// </summary>
public sealed class RenderQueue<TKey, TResult>(int threads, int archiveWindow, int interactiveCapacity) where TKey : notnull
{
    private readonly object _sync = new();
    private readonly Dictionary<TKey, Entry> _entries = new();
    private readonly List<Entry> _interactive = [];
    private readonly List<Entry> _archive = [];
    private int _running;
    private int _runningArchive;

    /// <summary>Сколько отрисовок сейчас идёт (для тестов).</summary>
    internal int Running
    {
        get
        {
            lock (_sync) return _running;
        }
    }

    /// <summary>
    ///     Дождаться результата отрисовки ключа: присоединиться к идущей или поставить новую. <paramref name="ct" /> отменяет только
    ///     ожидание этого запроса; отрисовка отменяется, когда ушли все ждущие.
    /// </summary>
    /// <exception cref="RenderQueueFullException">Новая интерактивная отрисовка, а очередь полна.</exception>
    public async Task<TResult> RunAsync(TKey key, ImagePriority priority, Func<CancellationToken, Task<TResult>> work, CancellationToken ct)
    {
        Entry entry;
        lock (_sync)
        {
            if (_entries.TryGetValue(key, out var existing))
            {
                entry = existing;
                // Картинку ждёт человек — архивная отрисовка становится интерактивной.
                if (priority == ImagePriority.Interactive && entry.Priority == ImagePriority.Archive && !entry.Started)
                {
                    _archive.Remove(entry);
                    _interactive.Add(entry);
                    entry.Priority = ImagePriority.Interactive;
                }
            }
            else
            {
                if (priority == ImagePriority.Interactive && _interactive.Count >= interactiveCapacity) throw new RenderQueueFullException();
                entry = new Entry(key, priority, work);
                _entries.Add(key, entry);
                (priority == ImagePriority.Interactive ? _interactive : _archive).Add(entry);
            }

            entry.Waiters++;
            Pump();
        }

        try
        {
            return await entry.Result.Task.WaitAsync(ct);
        }
        finally
        {
            Leave(entry);
        }
    }

    private void Leave(Entry entry)
    {
        lock (_sync)
        {
            if (--entry.Waiters > 0 || entry.Result.Task.IsCompleted) return;
            entry.Cancel.Cancel();
            // Ключ освобождается сразу: следующий запрос той же картинки начнёт новую отрисовку, а не присоединится к отменённой.
            _entries.Remove(entry.Key);
            if (entry.Started) return; // начатая сама заметит отмену и освободит поток
            (entry.Priority == ImagePriority.Interactive ? _interactive : _archive).Remove(entry);
            entry.Result.TrySetCanceled();
        }
    }

    /// <summary>Под <see cref="_sync" />: занять свободные потоки ждущими отрисовками — сначала интерактивными.</summary>
    private void Pump()
    {
        while (_running < threads)
        {
            Entry next;
            if (_interactive.Count > 0) next = Take(_interactive);
            else if (_archive.Count > 0 && _runningArchive < archiveWindow) next = Take(_archive);
            else return;

            next.Started = true;
            _running++;
            if (next.Priority == ImagePriority.Archive) _runningArchive++;
            _ = Task.Run(() => ExecuteAsync(next));
        }
    }

    private static Entry Take(List<Entry> queue)
    {
        var entry = queue[0];
        queue.RemoveAt(0);
        return entry;
    }

    private async Task ExecuteAsync(Entry entry)
    {
        try
        {
            entry.Result.TrySetResult(await entry.Work(entry.Cancel.Token));
        }
        catch (OperationCanceledException)
        {
            entry.Result.TrySetCanceled();
        }
        catch (Exception exception)
        {
            entry.Result.TrySetException(exception);
        }
        finally
        {
            lock (_sync)
            {
                _running--;
                if (entry.Priority == ImagePriority.Archive) _runningArchive--;
                // Брошенную отрисовку ключ уже не держит — под ним может идти новая.
                if (_entries.TryGetValue(entry.Key, out var current) && current == entry) _entries.Remove(entry.Key);
                Pump();
            }

            entry.Cancel.Dispose();
        }
    }

    private sealed class Entry(TKey key, ImagePriority priority, Func<CancellationToken, Task<TResult>> work)
    {
        public TKey Key { get; } = key;
        public ImagePriority Priority { get; set; } = priority;
        public Func<CancellationToken, Task<TResult>> Work { get; } = work;
        public TaskCompletionSource<TResult> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenSource Cancel { get; } = new();
        public int Waiters { get; set; }
        public bool Started { get; set; }
    }
}
