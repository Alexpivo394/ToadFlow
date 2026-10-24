using System.Text.Json;
using Alexpivo394.Revit.ParamCheck.Profiles;
using Microsoft.EntityFrameworkCore;
using ToadFlow.Contracts;
using ToadFlow.Contracts.Loi;
using ToadFlow.Server.Data;
using ToadFlow.Server.Data.Entities;
using ToadFlow.Server.Events;
using ToadFlow.Server.Modules;
using ToadFlow.Server.Schedules;
using ToadFlow.Server.Settings;

namespace ToadFlow.Server.Loi;

/// <summary>
///     Модуль «Проверка LOI»: профили ParamChecker, задания агентам на проверку одной модели и каталог категорий и параметров,
///     который снимает Revit на агенте, — для редактора правил в браузере.
/// </summary>
public sealed class LoiServerModule : IServerModule
{
    public string Kind => ModuleKinds.Loi;

    public void AddServices(IServiceCollection services) => services.AddScoped<LoiCatalogRefresher>();

    public void ConfigureModel(ModelBuilder model)
    {
        model.Entity<LoiProfile>(profile =>
        {
            profile.ToTable("loi_profiles");
            profile.HasIndex(p => p.Name).IsUnique();
            profile.Property(p => p.ProfileJson).HasColumnType("jsonb");
        });

        model.Entity<LoiCatalog>(catalog =>
        {
            catalog.ToTable("loi_catalogs");
            catalog.HasKey(c => c.RevitYear);
            catalog.Property(c => c.RevitYear).ValueGeneratedNever();
            catalog.Property(c => c.Catalog).AsJson();
        });
    }

    public void MapEndpoints(RouteGroupBuilder api) => api.MapLoi();

    public async Task<IReadOnlyList<string>> ValidateSettingsAsync(JsonElement settings, IServiceProvider services, CancellationToken ct)
    {
        if (LoiScheduleSettings.TryParse(settings) is not { } parsed) return ["Выберите профиль проверки LOI"];

        var stored = await services.GetRequiredService<ToadFlowDb>().Set<LoiProfile>().AsNoTracking().SingleOrDefaultAsync(p => p.Id == parsed.ProfileId, ct);
        if (stored is null) return ["Профиль проверки LOI не найден"];

        var errors = RunValidator.RuleProblems(stored.ToProfile()).Select(problem => problem.TrimEnd('.')).ToList();
        var history = parsed.HistoryFile.Trim();
        if (history.Length > 0)
        {
            if (!history.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)) errors.Add("База для дашбордов — файл Excel .xlsx");
            if (OutputFolderTemplate.Validate(history) is { } templateError) errors.Add($"База для дашбордов: {templateError}");
        }

        return errors;
    }

    public async Task<string> BuildPayloadAsync(JobBuildContext context, IServiceProvider services, CancellationToken ct)
    {
        var parsed = LoiScheduleSettings.TryParse(context.ModuleSettings) ?? throw new InvalidOperationException("Не выбран профиль проверки LOI");
        var db = services.GetRequiredService<ToadFlowDb>();
        var stored = await db.Set<LoiProfile>().AsNoTracking().SingleAsync(p => p.Id == parsed.ProfileId, ct);
        var profile = stored.ToProfile();

        var history = parsed.HistoryFile.Trim();
        if (history.Length > 0)
        {
            var timeZone = (await services.GetRequiredService<ISettingsStore>().GetAsync(ct)).TimeZone;
            var scheduleName = await db.Schedules.Where(s => s.Id == context.ScheduleId).Select(s => s.Name).SingleOrDefaultAsync(ct) ?? "";
            history = OutputFolderTemplate.Render(history, context.RunStartedAt, timeZone, scheduleName);
        }

        var model = context.Model;
        var payload = new LoiJobPayload(model.Name, model.ModelPath, model.IsServer, context.RevitYear, context.OutputFolder, context.RunStartedAt,
            stored.ProfileJson, parsed.Resolve(profile, history));
        return JsonSerializer.Serialize(payload, ToadFlowDb.Json);
    }

    /// <summary>Каталог, снятый Revit, — в таблицу каталогов: редактор правил видит его сразу.</summary>
    public async Task OnJobCompletedAsync(Job job, IServiceProvider services, CancellationToken ct)
    {
        if (job is not { ModuleKind: ModuleKinds.LoiCatalog, Status: JobStatus.Succeeded, ResultJson: { } json, RevitYear: { } year }) return;

        var result = JsonSerializer.Deserialize<LoiCatalogResult>(json, ToadFlowDb.Json);
        if (result is null) return;

        var db = services.GetRequiredService<ToadFlowDb>();
        var catalog = await db.Set<LoiCatalog>().SingleOrDefaultAsync(c => c.RevitYear == year, ct);
        if (catalog is null) db.Set<LoiCatalog>().Add(catalog = new LoiCatalog { RevitYear = year });
        catalog.Catalog = result;
        catalog.UpdatedAt = services.GetRequiredService<TimeProvider>().GetUtcNow();
        await db.SaveChangesAsync(ct);

        services.GetRequiredService<IDomainEvents>().Publish(new CatalogChanged(Actor.System, LoiEndpoints.CatalogEntity, Guid.Empty,
            $"Обновлён каталог Revit {year} для профилей LOI: категорий {result.Categories.Length}, параметров {result.Parameters.Length}"));
    }
}
