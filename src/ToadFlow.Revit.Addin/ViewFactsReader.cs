using Autodesk.Revit.DB;
using ToadFlow.Revit.Addin.Logic;

namespace ToadFlow.Revit.Addin;

/// <summary>
///     Подробности о виде выгрузки (<see cref="ViewFacts" />) для журнала задания, когда вид оказался пустым. Только счётчики
///     быстрых фильтров: элементы не разворачиваются.
/// </summary>
internal static class ViewFactsReader
{
    /// <returns>null — вида с таким именем нет.</returns>
    public static ViewFacts? Read(Document document, string viewName)
    {
        var name = viewName.Trim();
        var view = new FilteredElementCollector(document)
            .OfClass(typeof(View3D))
            .Cast<View3D>()
            .FirstOrDefault(candidate => !candidate.IsTemplate && string.Equals(candidate.Name, name, StringComparison.CurrentCultureIgnoreCase));
        if (view is null) return null;

        var categories = document.Settings.Categories.Cast<Category>()
            .Where(category => category.CategoryType == CategoryType.Model)
            .Select(category => category.Id)
            .ToList();
        var modelElements = Count(new FilteredElementCollector(document), categories);
        var inView = Count(new FilteredElementCollector(document, view.Id), categories);

        var worksets = document.IsWorkshared
            ? new FilteredWorksetCollector(document).OfKind(WorksetKind.UserWorkset).ToWorksets()
            : [];
        var template = view.ViewTemplateId == ElementId.InvalidElementId ? null : document.GetElement(view.ViewTemplateId)?.Name;
        var phase = view.get_Parameter(BuiltInParameter.VIEW_PHASE)?.AsValueString();

        return new ViewFacts(view.Name, template, string.IsNullOrEmpty(phase) ? null : phase, view.IsSectionBoxActive, view.IsTemporaryHideIsolateActive(),
            worksets.Count, worksets.Count(workset => workset.IsOpen), worksets.Count(workset => view.IsWorksetVisible(workset.Id)), modelElements, inView);
    }

    private static int Count(FilteredElementCollector collector, ICollection<ElementId> categories) =>
        categories.Count == 0 ? 0 : collector.WherePasses(new ElementMulticategoryFilter(categories)).WhereElementIsNotElementType().GetElementCount();
}
