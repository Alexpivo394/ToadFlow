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

        var year = int.Parse(Application.ControlledApplication.VersionNumber);
        var progress = new PipeProgress();
        var executor = new JobExecutor(year, progress);
        _client = new AgentPipeClient(instanceId, year, OptionalFunctionalityUtils.IsNavisworksExporterAvailable(), executor,
            Autodesk.Revit.UI.ExternalEvent.Create(executor), progress);
        _client.Start();
    }

    public override void OnShutdown() => _client?.Dispose();
}
