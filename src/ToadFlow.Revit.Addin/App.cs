using System.Diagnostics;
using Alexpivo394.Revit.Export.Core;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using Nice3point.Revit.Toolkit.External;
using ToadFlow.Revit.Addin.Logic;

namespace ToadFlow.Revit.Addin;

/// <summary>
///     Точка входа аддина. Работает только в Revit, запущенном агентом ToadFlow (есть TOADFLOW_INSTANCE_ID):
///     подключается к пайпу агента и выполняет его задания. В Revit, открытом руками, ничего не делает.
/// </summary>
public sealed class App : ExternalApplication
{
    /// <summary>Сколько ждать первого простоя Revit для прогрева; дольше — агенту сообщается «готов» без прогрева.</summary>
    private static readonly TimeSpan WarmupTimeout = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan NudgeEvery = TimeSpan.FromSeconds(1);

    private readonly ReadyGate _ready = new();
    private AgentPipeClient? _client;
    private JobExecutor? _executor;
    private Timer? _warmupTimeout;
    private Timer? _nudge;

    public override void OnStartup()
    {
        if (AddinBootstrap.InstanceIdFromProcess() is not { } instanceId) return;

        // Копии моделей и файлы выгрузок, брошенные убитым или упавшим Revit; занятые другим экземпляром папки остаются.
        TempWorkspace.CleanupUnused();

        var year = int.Parse(Application.ControlledApplication.VersionNumber);
        var progress = new PipeProgress();
        _executor = new JobExecutor(year, progress);
        _client = new AgentPipeClient(instanceId, year, OptionalFunctionalityUtils.IsNavisworksExporterAvailable(),
            Application.ControlledApplication.RecordingJournalFilename, _executor,
            Autodesk.Revit.UI.ExternalEvent.Create(_executor), progress);
        _client.Start();
        Application.ControlledApplication.ApplicationInitialized += OnApplicationInitialized;
    }

    public override void OnShutdown()
    {
        Application.ControlledApplication.ApplicationInitialized -= OnApplicationInitialized;
        Application.Idling -= OnFirstIdling;
        StopTimers();
        _client?.Dispose();
    }

    /// <summary>
    ///     Revit закончил запуск. «Готов» уходит агенту после прогрева (<see cref="OnFirstIdling" />): первая модель в только
    ///     что запущенном Revit часто открывается с пустым 3D-видом, а следующая — нормально. Если Revit не простаивает
    ///     <see cref="WarmupTimeout" />, «готов» уходит без прогрева.
    /// </summary>
    private void OnApplicationInitialized(object? sender, ApplicationInitializedEventArgs args)
    {
        Application.Idling += OnFirstIdling;
        _warmupTimeout = new Timer(_ => MarkReady($"прогрев Revit пропущен: Revit не простаивал {WarmupTimeout.TotalSeconds:0} с"), null, WarmupTimeout,
            Timeout.InfiniteTimeSpan);
        // Простой наступает, только когда в цикле сообщений Revit что-то происходит, — будим его, пока прогрев не прошёл.
        _nudge = new Timer(_ => AgentPipeClient.Nudge(), null, TimeSpan.Zero, NudgeEvery);
    }

    /// <summary>
    ///     Первый простой Revit: другие аддины доделали свой запуск. Создаём и закрываем пустой проект — Revit прогревается
    ///     до первой модели задания.
    /// </summary>
    private void OnFirstIdling(object? sender, IdlingEventArgs args)
    {
        Application.Idling -= OnFirstIdling;
        if (sender is not UIApplication application) return;

        var watch = Stopwatch.StartNew();
        string note;
        try
        {
            var document = application.Application.NewProjectDocument(UnitSystem.Metric);
            document.Close(false);
            note = $"прогрев Revit: пустой проект создан и закрыт за {watch.Elapsed.TotalSeconds:0.#} с";
        }
        catch (Exception exception) when (exception is Autodesk.Revit.Exceptions.ApplicationException or InvalidOperationException)
        {
            note = $"прогрев Revit не удался: {exception.Message}";
        }

        MarkReady(note);
    }

    /// <summary>Один раз: строку о прогреве — в журнал первого задания, «готов» — агенту.</summary>
    private void MarkReady(string note)
    {
        if (!_ready.TryOpen()) return;
        StopTimers();
        _executor?.SetStartupNote(note);
        _client?.MarkReady();
    }

    private void StopTimers()
    {
        _warmupTimeout?.Dispose();
        _nudge?.Dispose();
    }
}
