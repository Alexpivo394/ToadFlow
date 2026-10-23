using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ToadFlow.Contracts;
using ToadFlow.Contracts.Export;
using ToadFlow.Server.Data;
using ToadFlow.Server.Modules;

namespace ToadFlow.Server.Export;

/// <summary>Модуль «Выгрузка RVT/NWC»: пресеты, настройки расписания и задания агентам на выгрузку одной модели.</summary>
public sealed class ExportServerModule : IServerModule
{
    public string Kind => ModuleKinds.Export;

    public void AddServices(IServiceCollection services) => services.AddHostedService<DefaultPresetSeeder>();

    public void ConfigureModel(ModelBuilder model) => model.Entity<ExportPreset>(preset =>
    {
        preset.ToTable("export_presets");
        preset.HasIndex(p => p.Name).IsUnique();
        preset.Property(p => p.Settings).AsJson();
    });

    public void MapEndpoints(RouteGroupBuilder api) => api.MapPresets();

    public async Task<IReadOnlyList<string>> ValidateSettingsAsync(JsonElement settings, IServiceProvider services, CancellationToken ct)
    {
        if (ExportScheduleSettings.TryParse(settings) is not { } parsed) return ["Не заданы параметры выгрузки"];

        var preset = await services.GetRequiredService<ToadFlowDb>().Set<ExportPreset>().AsNoTracking().SingleOrDefaultAsync(p => p.Id == parsed.PresetId, ct);
        if (preset is null) return ["Пресет выгрузки не найден"];

        var merged = SettingsMerge.Apply(preset.Settings, parsed.Overrides);
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(merged.NwcViewName)) errors.Add("Укажите имя 3D-вида для NWC");
        if (!(merged.NwcFacetingFactor > 0)) errors.Add("Коэффициент фасетизации NWC должен быть больше нуля");
        return errors;
    }

    public async Task<string> BuildPayloadAsync(JobBuildContext context, IServiceProvider services, CancellationToken ct)
    {
        var parsed = ExportScheduleSettings.TryParse(context.ModuleSettings)
                     ?? throw new InvalidOperationException("Не заданы параметры выгрузки");
        var preset = await services.GetRequiredService<ToadFlowDb>().Set<ExportPreset>().AsNoTracking().SingleAsync(p => p.Id == parsed.PresetId, ct);

        var model = context.Model;
        var payload = new ExportJobPayload(model.Name, model.ModelPath, model.IsServer, context.RevitYear, model.Rvt, model.Nwc,
            context.OutputFolder, SettingsMerge.Apply(preset.Settings, parsed.Overrides));
        return JsonSerializer.Serialize(payload, ToadFlowDb.Json);
    }
}

/// <summary>Пресет «Стандарт» с настройками по умолчанию — если пресетов ещё нет.</summary>
internal sealed class DefaultPresetSeeder(IServiceScopeFactory scopes) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ToadFlowDb>();
        if (await db.Set<ExportPreset>().AnyAsync(ct)) return;

        db.Set<ExportPreset>().Add(new ExportPreset { Id = Guid.CreateVersion7(), Name = "Стандарт", IsDefault = true, Settings = new ExportSettingsDto() });
        await db.SaveChangesAsync(ct);
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
