namespace ToadFlow.Agent.Server;

/// <summary>Можно ли взять ещё одно задание по памяти, и почему.</summary>
public sealed record MemoryDecision(bool Allowed, string Reason);

/// <summary>
///     Допуск нового задания по физической памяти. Лимиты экземпляров по версиям остаются как есть — память лишь может
///     запретить взять задание, на которое лимит ещё есть.
///     <para>
///         Свободная память сейчас — не вся правда: уже работающие Revit ещё вырастут. Поэтому из свободной вычитается
///         ожидаемый рост каждого занятого экземпляра (до его пика за задание или до оценки на задание, что больше),
///         потом память на новое задание, — и должно остаться не меньше резерва для Windows.
///     </para>
///     <para>
///         Если на машине нет ни одного занятого Revit, одно задание берётся всегда: иначе машина, где память заняли другие
///         программы, не взяла бы ничего никогда. Других агентов это не блокирует — задание просто ждёт в общей очереди.
///     </para>
/// </summary>
public static class MemoryGate
{
    /// <param name="busy">Занятые и запускающиеся экземпляры: сколько занимают сейчас и наибольшее за текущее задание.</param>
    /// <param name="idleReuseMb">Задание достанется простаивающему Revit, который уже занимает столько; null — запустится новый.</param>
    /// <param name="perJobMb">Сколько памяти закладывать на задание.</param>
    /// <param name="reserveMb">Сколько памяти не отдавать Revit.</param>
    public static MemoryDecision Check(RamSnapshot ram, IReadOnlyCollection<(long CurrentMb, long PeakMb)> busy, long? idleReuseMb, long perJobMb,
        long reserveMb)
    {
        var growth = busy.Sum(b => Math.Max(0, Math.Max(b.PeakMb, perJobMb) - b.CurrentMb));
        var need = idleReuseMb is { } idle ? Math.Max(0, perJobMb - idle) : perJobMb;
        var left = ram.AvailableMb - growth - need;
        var numbers = $"свободно {ram.AvailableMb} МБ, ещё вырастут занятые Revit на {growth} МБ, заданию нужно {need} МБ, резерв {reserveMb} МБ";

        if (left >= reserveMb) return new MemoryDecision(true, numbers);
        if (busy.Count == 0) return new MemoryDecision(true, $"памяти мало, но агент свободен — берём одно задание: {numbers}");
        return new MemoryDecision(false, $"не хватает памяти на ещё один Revit: {numbers}");
    }
}
