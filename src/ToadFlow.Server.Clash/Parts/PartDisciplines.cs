using Microsoft.EntityFrameworkCore;
using ToadFlow.Server.Data;
using ToadFlow.Server.Naming;

namespace ToadFlow.Server.Clash.Parts;

/// <summary>Разделы, заданные вручную у моделей частей: их нет в разборе имён, но коллизии по ним есть.</summary>
public sealed class PartDisciplines(ToadFlowDb db) : IDisciplineSource
{
    public async Task<IReadOnlyList<string>> DisciplinesAsync(CancellationToken ct) =>
        await db.Set<ClashPartModel>().AsNoTracking().Where(m => m.DisciplineOverride != null).Select(m => m.DisciplineOverride!).Distinct().ToListAsync(ct);
}
