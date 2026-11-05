using System.Globalization;
using Alexpivo394.Revit.Export.Core;
using ToadFlow.Contracts;

namespace ToadFlow.Revit.Addin.Logic;

/// <summary>Что было на виде выгрузки, когда он оказался пустым. Собирает аддин в Revit, читает человек в журнале задания.</summary>
/// <param name="Template">Шаблон вида; null — без шаблона.</param>
/// <param name="Phase">Стадия вида; null — не задана.</param>
/// <param name="Worksets">Пользовательских рабочих наборов; 0 — модель без совместной работы.</param>
/// <param name="VisibleWorksets">Рабочих наборов, видимых на виде.</param>
/// <param name="ModelElements">Экземпляров модельных категорий в документе.</param>
/// <param name="ModelElementsInView">Из них видимых на виде.</param>
/// <remarks>Класс, а не record: аддин собирается и под net48, где нет init-свойств.</remarks>
public sealed class ViewFacts(
    string ViewName,
    string? Template,
    string? Phase,
    bool SectionBox,
    bool TemporaryHide,
    int Worksets,
    int OpenWorksets,
    int VisibleWorksets,
    int ModelElements,
    int ModelElementsInView)
{
    public string ViewName { get; } = ViewName;
    public string? Template { get; } = Template;
    public string? Phase { get; } = Phase;
    public bool SectionBox { get; } = SectionBox;
    public bool TemporaryHide { get; } = TemporaryHide;
    public int Worksets { get; } = Worksets;
    public int OpenWorksets { get; } = OpenWorksets;
    public int VisibleWorksets { get; } = VisibleWorksets;
    public int ModelElements { get; } = ModelElements;
    public int ModelElementsInView { get; } = ModelElementsInView;
}

/// <summary>
///     Пустой вид выгрузки. Первая модель в только что запущенном Revit часто открывается так, что на 3D-виде почти ничего
///     не видно, хотя элементы в модели есть, — следующая модель в том же Revit открывается нормально. Поэтому пустой вид
///     сначала повторяется в этом же Revit и только потом — в новом.
/// </summary>
public static class EmptyView
{
    private static readonly NumberFormatInfo Thousands = new() { NumberGroupSeparator = " ", NumberGroupSizes = [3] };

    /// <summary>Выгрузка для коллизий не нашла на виде ни одного элемента, хотя в модели они есть.</summary>
    public static bool IsEmpty(int elements, int inventory) => elements == 0 && inventory > 0;

    /// <summary>
    ///     NWC не получился, хотя модель открылась и вид есть (<see cref="ModelResult.NwcMissing" />), — повторить выгрузку в этом
    ///     же Revit. Внутренняя ошибка Revit идёт в новый Revit, как раньше; пропуск пользователем не повторяется.
    /// </summary>
    public static bool RetryNwc(ModelResult model) => model is { NwcMissing: true, RevitFailure: false, SkippedByUser: false };

    /// <summary>
    ///     Итог выгрузки для коллизий, вид которой остался пустым и после повтора в этом же Revit: пока есть попытки — сбой
    ///     инфраструктуры (новый Revit), на последней — ошибка. Пустой слепок на сервер не уходит: иначе коллизии модели
    ///     молча пропадают.
    /// </summary>
    public static (JobOutcome Outcome, string Note) ClashOutcome(bool canRetry, string viewName, int inventory)
    {
        var note = $"на виде «{viewName}» нет видимых элементов, хотя в модели их {Number(inventory)}";
        return canRetry ? (JobOutcome.InfrastructureFailure, note + " — модель повторится в новом Revit") : (JobOutcome.Failed, note);
    }

    /// <summary>Строка журнала: что скрывает элементы на виде.</summary>
    public static string Describe(ViewFacts facts)
    {
        var worksets = facts.Worksets == 0
            ? "без рабочих наборов"
            : $"рабочие наборы: открыто {facts.OpenWorksets} из {facts.Worksets}, видно на виде {facts.VisibleWorksets}";
        var template = facts.Template is null ? "без шаблона" : $"шаблон «{facts.Template}»";
        var phase = facts.Phase is null ? "стадия не задана" : $"стадия «{facts.Phase}»";
        return $"вид «{facts.ViewName}»: элементов модели на виде {Number(facts.ModelElementsInView)} из {Number(facts.ModelElements)}; {worksets}; " +
               $"{template}, {phase}, 3D-подрезка {OnOff(facts.SectionBox)}, временное скрытие {OnOff(facts.TemporaryHide)}";
    }

    private static string OnOff(bool value) => value ? "вкл." : "выкл.";

    private static string Number(long value) => value.ToString("#,0", Thousands);
}
