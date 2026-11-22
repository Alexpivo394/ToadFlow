using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ToadFlow.Server.Data;
using ToadFlow.Server.Data.Entities;

namespace ToadFlow.Server.Overview;

/// <summary>
///     Когда ошибки модуля отметили решёнными: ошибки, закончившиеся раньше, «Требует внимания» больше не считает. Хранится строкой
///     таблицы настроек на модуль.
/// </summary>
public sealed class AttentionResolutions(ToadFlowDb db)
{
    private const string Prefix = "attention-resolved:";

    /// <summary>Модуль → момент «решено».</summary>
    public async Task<IReadOnlyDictionary<string, DateTimeOffset>> ListAsync(CancellationToken ct)
    {
        var rows = await db.Settings.AsNoTracking().Where(s => s.Key.StartsWith(Prefix)).ToListAsync(ct);
        return rows.ToDictionary(r => r.Key[Prefix.Length..], r => JsonSerializer.Deserialize<DateTimeOffset>(r.Value));
    }

    public async Task ResolveAsync(string module, DateTimeOffset at, CancellationToken ct)
    {
        var key = Prefix + module;
        var row = await db.Settings.SingleOrDefaultAsync(s => s.Key == key, ct);
        if (row is null) db.Settings.Add(row = new Setting { Key = key });
        row.Value = JsonSerializer.Serialize(at);
        await db.SaveChangesAsync(ct);
    }
}
