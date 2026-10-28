using Alexpivo394.Revit.Export.Core;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Nice3point.Revit.Toolkit.External;
using ToadFlow.Revit.Addin.Logic;

namespace ToadFlow.Revit.Addin;

/// <summary>
///     Точка входа аддина. Работает только в Revit, запущенном агентом ToadFlow (есть TOADFLOW_INSTANCE_ID):
///     подключается к пайпу агента и выполняет его задания. В Revit, открытом руками, ничего не делает.
/// </summary>
public sealed class App : ExternalApplication
{
    private AgentPipeClient? _client;

    public override void OnStartup()
    {
        if (AddinBootstrap.InstanceIdFromProcess() is not { } instanceId) return;

        // Копии моделей и файлы выгрузок, брошенные убитым или упавшим Revit; занятые другим экземпляром папки остаются.
        TempWorkspace.CleanupUnused();

        var year = int.Parse(Application.ControlledApplication.VersionNumber);
        var progress = new PipeProgress();
        var executor = new JobExecutor(year, progress);
        _client = new AgentPipeClient(instanceId, year, OptionalFunctionalityUtils.IsNavisworksExporterAvailable(),
            Application.ControlledApplication.RecordingJournalFilename, executor,
            Autodesk.Revit.UI.ExternalEvent.Create(executor), progress);
        _client.Start();
        Application.ControlledApplication.ApplicationInitialized += OnApplicationInitialized;
    }

    public override void OnShutdown()
    {
        Application.ControlledApplication.ApplicationInitialized -= OnApplicationInitialized;
        _client?.Dispose();
    }

    /// <summary>Revit закончил запуск: остальные аддины загружены, стартовые окна закрыты — агент может слать задания.</summary>
    private void OnApplicationInitialized(object? sender, ApplicationInitializedEventArgs args) => _client?.MarkReady();
}
