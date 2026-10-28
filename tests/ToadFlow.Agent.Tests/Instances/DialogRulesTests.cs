using Shouldly;
using ToadFlow.Agent.Instances;

namespace ToadFlow.Agent.Tests.Instances;

public class DialogRulesTests
{
    private const string Location = @"C:\ProgramData\Autodesk\Revit\Addins\2022\ToadFlow.Revit.Addin\ToadFlow.Revit.Addin.dll";

    private static WindowSnapshot Window(string title, string[] texts, string[] buttons, bool progress = false) => new(1, title, texts, buttons, progress);

    public static IEnumerable<Func<(WindowSnapshot Window, DialogKind Kind)>> Cases() =>
    [
        () => (Window("Безопасность — Надстройка без подписи", ["Не удалось проверить издателя этой надстройки.", $"Расположение: {Location}"],
            ["Всегда загружать", "Загрузить один раз", "Не загружать"]), DialogKind.UnsignedToadFlowAddin),
        () => (Window("Security - Unsigned Add-In", ["The publisher of this add-in could not be verified.", $"Location: {Location}"],
            ["Always Load", "Load Once", "Do Not Load"]), DialogKind.UnsignedToadFlowAddin),
        () => (Window("Безопасность — Надстройка без подписи", [@"Расположение: C:\ProgramData\Autodesk\Revit\Addins\2022\Other\Other.dll"],
            ["Всегда загружать", "Загрузить один раз", "Не загружать"]), DialogKind.UnsignedAddin),
        () => (Window("Security - Unsigned Add-In", [@"Location: C:\Addins\Vendor.dll"], ["Always Load", "Load Once", "Do Not Load"]), DialogKind.UnsignedAddin),
        () => (Window("Внешние инструменты", [$"Не удалось инициализировать надстройку «ToadFlow»: {Location}"], ["Закрыть"]), DialogKind.ToadFlowAddinFailure),
        () => (Window("Autodesk Revit 2024", ["Revit has encountered a serious error and will be closed."], ["Save", "Close"]), DialogKind.RevitFatalError),
        () => (Window("Autodesk Revit 2022", ["В Revit произошла серьёзная ошибка."], ["Сохранить", "Закрыть"]), DialogKind.RevitFatalError),
        () => (Window("Revit", ["Hardware acceleration is not available. Revit will use software rendering."], ["OK", "Show details"]),
            DialogKind.HardwareAcceleration),
        () => (Window("Revit", ["Аппаратное ускорение недоступно."], ["ОК", "Подробнее"]), DialogKind.HardwareAcceleration),
        () => (Window("Revit", ["The family template path is invalid."], ["OK", "Show details"]), DialogKind.FamilyTemplatePath),
        () => (Window("Revit", ["Неверный путь к шаблонам семейств."], ["ОК", "Подробнее"]), DialogKind.FamilyTemplatePath),
        () => (Window("Navisworks", ["Не найдена подходящая геометрия."], ["ОК"]), DialogKind.Message),
        () => (Window("Autodesk Revit 2022", ["Сохранить изменения в «АР.rvt»?"], ["Да", "Нет", "Отмена"]), DialogKind.Unknown),
        () => (Window("Экспорт", ["Экспорт NWC"], ["Отмена"], progress: true), DialogKind.Progress),
        () => (Window("Revit", [], []), DialogKind.Progress)
    ];

    [Test]
    [MethodDataSource(nameof(Cases))]
    public void Classify_KnownWindows((WindowSnapshot Window, DialogKind Kind) known) => DialogRules.Classify(known.Window).ShouldBe(known.Kind);

    [Test]
    public void Classify_Unreadable_IsUnknown() =>
        DialogRules.Classify(new WindowSnapshot(1, "Безопасность — Надстройка без подписи", [], [], false, false)).ShouldBe(DialogKind.Unknown);

    [Test]
    [Arguments(DialogKind.UnsignedToadFlowAddin, DialogResponse.Answer)]
    [Arguments(DialogKind.HardwareAcceleration, DialogResponse.Answer)]
    [Arguments(DialogKind.FamilyTemplatePath, DialogResponse.Answer)]
    [Arguments(DialogKind.Message, DialogResponse.Answer)]
    [Arguments(DialogKind.UnsignedAddin, DialogResponse.AskPerson)]
    [Arguments(DialogKind.Unknown, DialogResponse.AskPerson)]
    [Arguments(DialogKind.ToadFlowAddinFailure, DialogResponse.Fail)]
    [Arguments(DialogKind.RevitFatalError, DialogResponse.Fail)]
    [Arguments(DialogKind.Progress, DialogResponse.Ignore)]
    public void Respond_ByKind(DialogKind kind, DialogResponse response) => DialogRules.Respond(kind).Response.ShouldBe(response);

    [Test]
    public void Respond_UnsignedToadFlowAddin_AlwaysLoad() => DialogRules.Respond(DialogKind.UnsignedToadFlowAddin).Action.ShouldBe(DialogAction.AlwaysLoad);

    [Test]
    public void Describe_LongText_Truncated() =>
        Window("Revit", [new string('х', 2000)], ["ОК"]).Describe().Length.ShouldBeLessThanOrEqualTo(401);
}
