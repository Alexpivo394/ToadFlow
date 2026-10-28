using System;
using System.Collections.Generic;

namespace ToadFlow.Contracts.Clash;

/// <summary>Прогон для работника движка: снимок конфигурации и слепки моделей части на момент «Прогнать».</summary>
/// <param name="ConfigurationJson">Конфигурация в формате <c>ConfigurationJson</c> из <c>Alexpivo394.Revit.Clash.Rules</c>.</param>
public sealed record EngineRunAssignment(Guid RunId, string ConfigurationJson, IReadOnlyList<EngineRunModel> Models, int Attempt);

/// <param name="ModelId">Модель ToadFlow; в слепке она же — <c>ModelKey</c> в форме <c>N</c>.</param>
public sealed record EngineRunModel(Guid ModelId, string ModelName, string Discipline, Guid SnapshotId, long GeometryBytes, long ParametersBytes);

/// <summary>Ход прогона; заодно продлевает его аренду.</summary>
public sealed record EngineProgress(Guid RunId, string Stage, string Text);

/// <summary>
///     Коллизия с обеими сторонами. Пара каноническая: сначала элемент с меньшим (<c>ModelA</c>, <c>UniqueIdA</c>) в ordinal-порядке.
///     Координаты — мм в общей системе первой модели прогона.
/// </summary>
/// <param name="ModelA">Id модели в форме <c>N</c>.</param>
/// <param name="SetsA">Наборы элемента A его раздела, в которые он попал.</param>
public sealed record EngineHit(
    string ModelA,
    string UniqueIdA,
    long ElementIdA,
    string DisciplineA,
    IReadOnlyList<Guid> SetsA,
    string CategoryA,
    string? FamilyA,
    string? TypeA,
    string? LevelA,
    string ModelB,
    string UniqueIdB,
    long ElementIdB,
    string DisciplineB,
    IReadOnlyList<Guid> SetsB,
    string CategoryB,
    string? FamilyB,
    string? TypeB,
    string? LevelB,
    double X,
    double Y,
    double Z,
    float DepthMm);

/// <param name="LoadSeconds">Скачивание и чтение слепков.</param>
/// <param name="PeakWorkingSetBytes">Пик памяти процесса работника за прогон.</param>
public sealed record EngineRunStats(
    int Elements,
    long Triangles,
    long CandidatePairs,
    double LoadSeconds,
    double BroadSeconds,
    double NarrowSeconds,
    long PeakManagedBytes,
    long PeakWorkingSetBytes);

/// <summary>Часть результата прогона; последняя часть несёт статистику.</summary>
public sealed record EngineRunResult(Guid RunId, IReadOnlyList<EngineHit> Hits, EngineRunStats? Stats);

/// <param name="Retry">Сбой может не повториться (сеть, ввод-вывод): прогон вернётся в очередь, пока есть попытки.</param>
public sealed record EngineFailure(Guid RunId, string Error, bool Retry);

/// <summary>Правила протокола работника движка.</summary>
public static class EngineApi
{
    /// <summary>Заголовок с ключом работника (<c>Clash:EngineKey</c>).</summary>
    public const string KeyHeader = "X-Engine-Key";

    /// <summary>Сколько коллизий в одной части результата: сотни тысяч коллизий не идут одним телом запроса.</summary>
    public const int ResultChunkHits = 50_000;
}
