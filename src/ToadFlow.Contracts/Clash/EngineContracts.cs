using System;
using System.Collections.Generic;

namespace ToadFlow.Contracts.Clash;

/// <summary>Прогон для работника движка: снимок конфигурации и слепки моделей части на момент «Прогнать».</summary>
/// <param name="ConfigurationJson">Конфигурация в формате <c>ConfigurationJson</c> из <c>Alexpivo394.Revit.Clash.Rules</c>.</param>
/// <param name="Kind"><see cref="EngineApi.Draft" /> или <see cref="EngineApi.Approved" />: утверждённому прогону нужна основа (<see cref="EngineBasis" />).</param>
public sealed record EngineRunAssignment(Guid RunId, string ConfigurationJson, IReadOnlyList<EngineRunModel> Models, int Attempt, string Kind = EngineApi.Draft);

/// <param name="ModelId">Модель ToadFlow; в слепке она же — <c>ModelKey</c> в форме <c>N</c>.</param>
public sealed record EngineRunModel(Guid ModelId, string ModelName, string Discipline, Guid SnapshotId, long GeometryBytes, long ParametersBytes);

/// <summary>Ход прогона; заодно продлевает его аренду.</summary>
public sealed record EngineProgress(Guid RunId, string Stage, string Text);

/// <summary>
///     Коллизия с обеими сторонами. Пара каноническая: сначала элемент с меньшим (<c>ModelA</c>, <c>UniqueIdA</c>) в ordinal-порядке.
///     Точка — центр зоны пересечения, абсолютные мм в общей системе координат моделей.
/// </summary>
/// <param name="ModelA">Id модели в форме <c>N</c>.</param>
/// <param name="SetsA">Наборы элемента A, в которые он попал.</param>
/// <param name="Checks">Проверки (клетки матрицы) коллизии: ключи <c>{группа A:N}|{группа B:N}</c>, меньший Id первым.</param>
/// <param name="FingerprintA">Отпечаток геометрии элемента A (<c>ulong</c> теми же битами).</param>
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
    float DepthMm,
    IReadOnlyList<string> Checks,
    long FingerprintA = 0,
    long FingerprintB = 0);

/// <param name="LoadSeconds">Скачивание и чтение слепков.</param>
/// <param name="PeakWorkingSetBytes">Пик памяти процесса работника за прогон.</param>
/// <param name="ReusedPairs">Пары, взятые из прошлого утверждённого прогона без точной проверки.</param>
public sealed record EngineRunStats(
    int Elements,
    long Triangles,
    long CandidatePairs,
    double LoadSeconds,
    double BroadSeconds,
    double NarrowSeconds,
    long PeakManagedBytes,
    long PeakWorkingSetBytes,
    long ReusedPairs = 0);

/// <summary>Часть результата прогона; последняя часть несёт статистику, а у утверждённого прогона — ещё ушедшие пары и сопоставления.</summary>
/// <param name="Gone">Открытые пары основы, которых нет среди найденных, с фактами о них.</param>
/// <param name="Matches">Предложения сопоставить пересозданные элементы.</param>
public sealed record EngineRunResult(
    Guid RunId,
    IReadOnlyList<EngineHit> Hits,
    EngineRunStats? Stats,
    IReadOnlyList<EngineGonePair>? Gone = null,
    IReadOnlyList<EngineIdentityMatch>? Matches = null);

/// <summary>Основа утверждённого прогона: прошлый удачный прогон утверждённой версии части и открытые коллизии.</summary>
/// <param name="PreviousConfigurationJson">Конфигурация прошлого прогона; null — прошлого не было.</param>
/// <param name="PreviousModels">Слепки прошлого прогона.</param>
/// <param name="Open">Все коллизии части, кроме исправленных.</param>
public sealed record EngineBasis(string? PreviousConfigurationJson, IReadOnlyList<EngineRunModel> PreviousModels, IReadOnlyList<EngineOpenPair> Open);

/// <param name="FoundByPrevious">Найдена прошлым прогоном: точка, глубина и отпечатки — его.</param>
public sealed record EngineOpenPair(
    string ModelA,
    string UniqueIdA,
    string ModelB,
    string UniqueIdB,
    bool FoundByPrevious,
    double X,
    double Y,
    double Z,
    float DepthMm,
    long FingerprintA,
    long FingerprintB);

/// <summary>Открытая пара, которой нет среди найденных.</summary>
/// <param name="PresenceA"><c>InGeometry</c>, <c>InventoryOnly</c> (скрыт на виде) или <c>Absent</c> (удалён).</param>
/// <param name="PreviousConfigWouldHit">Конфигурация сменилась: нашла бы пару прошлая на нынешней геометрии; null — конфигурация та же или элемента нет.</param>
public sealed record EngineGonePair(string ModelA, string UniqueIdA, string ModelB, string UniqueIdB, string PresenceA, string PresenceB, bool? PreviousConfigWouldHit);

/// <summary>Элемент модели, вероятно, пересоздан: старый UniqueId → новый. Только предложение.</summary>
public sealed record EngineIdentityMatch(string Model, string OldUniqueId, string NewUniqueId);

/// <param name="Retry">Сбой может не повториться (сеть, ввод-вывод): прогон вернётся в очередь, пока есть попытки.</param>
public sealed record EngineFailure(Guid RunId, string Error, bool Retry);

/// <summary>Задание картинок части: прогон, коллизии которого рисуются, и его слепки.</summary>
public sealed record EngineImageAssignment(Guid PartId, Guid RunId, IReadOnlyList<EngineRunModel> Models);

/// <summary>Коллизия, которой нужна картинка. Точка — абсолютные мм в общей системе; отпечатки — те, по которым рисуется картинка.</summary>
/// <param name="ModelA">Id модели в форме <c>N</c> — <c>ModelKey</c> слепка.</param>
public sealed record EngineImageRequest(
    Guid CollisionId,
    string ModelA,
    string UniqueIdA,
    string ModelB,
    string UniqueIdB,
    double X,
    double Y,
    double Z,
    long FingerprintA,
    long FingerprintB);

/// <summary>Пачка коллизий задания; пустая без <paramref name="EngineRunWaiting" /> — рисовать больше нечего.</summary>
/// <param name="EngineRunWaiting">Прогон движка ждёт или идёт: работник отпускает задание (<see cref="EngineImageFailure.Yield" />), пачка пуста.</param>
public sealed record EngineImageBatch(Guid RunId, IReadOnlyList<EngineImageRequest> Items, bool EngineRunWaiting = false);

/// <summary>Строка манифеста сданной пачки: картинка коллизии нарисована (<paramref name="Ok" />) или нарисовать нельзя.</summary>
public sealed record EngineImageResult(Guid CollisionId, long FingerprintA, long FingerprintB, bool Ok);

/// <param name="Yield">Работник уступает задание прогону движка: попытка не засчитывается.</param>
public sealed record EngineImageFailure(string Error, bool Yield = false);

/// <summary>Правила протокола работника движка.</summary>
public static class EngineApi
{
    /// <summary>Заголовок с ключом работника (<c>Clash:EngineKey</c>).</summary>
    public const string KeyHeader = "X-Engine-Key";

    /// <summary>Сколько коллизий в одной части результата: сотни тысяч коллизий не идут одним телом запроса.</summary>
    public const int ResultChunkHits = 50_000;

    /// <summary>Прогон черновика.</summary>
    public const string Draft = "Draft";

    /// <summary>Прогон утверждённой версии.</summary>
    public const string Approved = "Approved";

    /// <summary>Сколько коллизий в одной пачке картинок.</summary>
    public const int ImageBatchSize = 200;

    /// <summary>Часть multipart-сдачи картинок с манифестом (JSON-массив <see cref="EngineImageResult" />).</summary>
    public const string ImageManifestPart = "manifest";
}
