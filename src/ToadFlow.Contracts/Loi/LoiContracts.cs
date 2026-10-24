using System;

namespace ToadFlow.Contracts.Loi;

/// <summary>Как открыть модель и что положить в отчёт — те же поля, что у <c>Alexpivo394.Revit.ParamCheck.Core.CheckOptions</c>.</summary>
public sealed record LoiCheckSettingsDto
{
    public bool SkipLinks { get; init; } = true;

    /// <summary>Рабочие наборы с этим словом в имени не открываются; пусто — открываются все.</summary>
    public string WorksetKeyword { get; init; } = "";

    /// <summary>Вид, с которого собираются элементы; пусто — вся модель.</summary>
    public string ViewName { get; init; } = "";

    /// <summary>В листах правил только элементы с незаполненными параметрами.</summary>
    public bool ProblemsOnly { get; init; }

    /// <summary>Книга Excel, куда дописываются итоги для дашбордов; пусто — не ведётся.</summary>
    public string HistoryFile { get; init; } = "";
}

/// <summary>Задание агенту на проверку одной модели.</summary>
/// <param name="RunStartedAt">Начало запуска — общее для всех его строк в базе для дашбордов.</param>
/// <param name="ProfileJson">Профиль в формате ParamChecker (<c>ProfileSerializer.SerializeProfile</c>), без моделей.</param>
public sealed record LoiJobPayload(
    string ModelName,
    string ModelPath,
    bool IsServer,
    int RevitYear,
    string OutputFolder,
    DateTimeOffset RunStartedAt,
    string ProfileJson,
    LoiCheckSettingsDto Settings);

/// <summary>Итоги проверки модели для истории: полнота — доля элементов, где заполнено всё; заполненность — доля значений.</summary>
public sealed record LoiJobResult(
    int Elements,
    int CompleteElements,
    double? Completeness,
    int Values,
    int Filled,
    int Empty,
    int Missing,
    double? FillRate,
    string? ReportPath,
    LoiRuleResultDto[] Rules);

public sealed record LoiRuleResultDto(string Title, int Elements, int CompleteElements, double? Completeness, double? FillRate);

/// <summary>Задание агенту: снять каталог категорий и параметров в Revit этого года.</summary>
public sealed record LoiCatalogPayload(int RevitYear);

/// <param name="Key">Имя <c>BuiltInCategory</c>, например <c>OST_DuctCurves</c> — так категория хранится в профиле.</param>
/// <param name="Name">Подпись на языке Revit, например «Воздуховоды».</param>
public sealed record LoiCategoryDto(string Key, string Name, bool IsModel);

/// <param name="HasSharedParameterFile">У Revit на агенте подключён ФОП: без него неизвестны ADSK-параметры, и подсвечивать их как опечатки нельзя.</param>
public sealed record LoiCatalogResult(LoiCategoryDto[] Categories, string[] Parameters, bool HasSharedParameterFile);
