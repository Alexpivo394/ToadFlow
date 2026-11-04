using ToadFlow.Server.Health;

namespace ToadFlow.Server.Clash.Engine;

/// <summary>
///     Когда работник движка последний раз обращался к серверу (взять прогон или сообщить ход) — в памяти сервера. По нему веб
///     пишет «Движок не отвечает N мин»: без него прогоны молча висят «в очереди», когда контейнер движка не поднялся или упал.
/// </summary>
public sealed class EngineLiveness(TimeProvider time)
{
    private long _lastSeen = long.MinValue;

    /// <summary>Когда сервер запустился: «движок не отвечает» без единого обращения считается от этого времени.</summary>
    public DateTimeOffset StartedAt { get; } = time.GetUtcNow();

    /// <summary>Последнее обращение работника; null — с запуска сервера не обращался.</summary>
    public DateTimeOffset? LastSeenAt
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastSeen);
            return ticks == long.MinValue ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    public void Seen() => Interlocked.Exchange(ref _lastSeen, time.GetUtcNow().UtcTicks);
}

/// <summary>Ответ <c>GET /api/clash/engine/status</c>: последнее обращение движка, запуск сервера и сколько прогонов ждут.</summary>
public sealed record EngineStatusView(DateTimeOffset? LastSeenAt, DateTimeOffset ServerStartedAt, int Queued);

/// <summary>Свободное место тома слепков коллизий в ответе <c>/api/health</c> (ГБ, одна цифра после запятой).</summary>
public sealed class ClashVolumeProbe(Microsoft.Extensions.Options.IOptions<ClashStorageOptions> options) : IHealthProbe
{
    public string Name => "clashVolumeFreeGb";

    public object? Probe()
    {
        // Папки ещё нет (ничего не выгружали) — место того тома, где она появится.
        var root = Path.GetFullPath(options.Value.Root);
        while (!Directory.Exists(root) && Path.GetDirectoryName(root) is { } parent) root = parent;
        if (!Directory.Exists(root)) return null;
        // На Linux DriveInfo берёт statvfs самого пути (том в контейнере), на Windows нужен корень диска.
        var drive = new DriveInfo(OperatingSystem.IsWindows() ? Path.GetPathRoot(root)! : root);
        return Math.Round(drive.AvailableFreeSpace / (1024.0 * 1024 * 1024), 1);
    }
}
