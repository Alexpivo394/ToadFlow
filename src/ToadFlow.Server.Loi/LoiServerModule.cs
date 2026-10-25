using System.Text.Json;
using Alexpivo394.Revit.ParamCheck.Profiles;
using Alexpivo394.Revit.ParamCheck.Summaries;
using Microsoft.EntityFrameworkCore;
using ToadFlow.Contracts;
using ToadFlow.Contracts.Loi;
using ToadFlow.Server.Data;
using ToadFlow.Server.Data.Entities;
using ToadFlow.Server.Events;
using ToadFlow.Server.Loi.Analytics;
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

    public void AddServices(IServiceCollection services)
    {
        services.AddScoped<LoiCatalogRefresher>();
        services.AddScoped<LoiSettingsStore>();
        services.AddScoped<CheckRecorder>();
        services.AddScoped<LoiProfileResolver>();
        services.AddHostedService<AnalyticsCleaner>();
    }

    public void ConfigureModel(ModelBuilder model)
    {
        model.Entity<LoiCheck>(check =>
        {
            check.ToTable("loi_checks");
            check.Property(c => c.Id).ValueGeneratedNever();
            check.Property(c => c.Fields).AsJson();
            check.HasIndex(c => c.CheckedAt);
            check.HasIndex(c => new { c.ModelKey, c.CheckedAt });
            check.HasIndex(c => new { c.Object, c.Discipline, c.CheckedAt });
            check.HasMany(c => c.Rules).WithOne().HasForeignKey(r => r.CheckId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<LoiCheckRule>(rule =>
        {
            rule.ToTable("loi_check_rules");
            rule.HasMany(r => r.Parameters).WithOne().HasForeignKey(p => p.RuleId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<LoiCheckParameter>(parameter =>
        {
            parameter.ToTable("loi_check_parameters");
            parameter.HasIndex(p => new { p.CheckId, p.Name });
            parameter.HasIndex(p => p.Name);
        });

        model.Entity<LoiModelOverride>(manual =>
        {
            manual.ToTable("loi_model_overrides");
            manual.HasKey(o => o.ModelKey);
        });

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

        var errors = new List<string>();
        if (parsed.ProfileId is null)
        {
            var routing = await services.GetRequiredService<LoiSettingsStore>().RoutingAsync(ct);
            if (routing.Disciplines.Count == 0 && routing.FallbackProfileId is null && parsed.ModelProfiles.Count == 0)
                errors.Add("Профили по разделам не назначены — задайте их в «Настройки → Проверка LOI» или выберите один профиль на расписание");
        }

        var duplicates = parsed.ModelProfiles.GroupBy(m => ModelNames.FromPath(m.Model), StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key)
            .ToList();
        if (duplicates.Count > 0) errors.Add($"Модели с двумя профилями: {string.Join(", ", duplicates)}");

        // Профили маршрутов проверяются при запуске: недописанный профиль раздела не должен мешать расписаниям других разделов.
        var ids = parsed.ProfileIds.Distinct().ToList();
        var stored = await services.GetRequiredService<ToadFlowDb>().Set<LoiProfile>().AsNoTracking().Where(p => ids.Contains(p.Id)).ToListAsync(ct);
        if (stored.Count < ids.Count) errors.Add("Профиль проверки LOI не найден");
        errors.AddRange(stored.SelectMany(p => RunValidator.RuleProblems(p.ToProfile())).Select(problem => problem.TrimEnd('.')));

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
        var model = context.Model;
        var choice = await services.GetRequiredService<LoiProfileResolver>().ChooseAsync(parsed, model.Name, model.ModelPath, ct);
        if (choice.ProfileId is not { } profileId) throw new ModelSkippedException(LoiProfileResolver.SkipReason(choice));

        var stored = await db.Set<LoiProfile>().AsNoTracking().SingleOrDefaultAsync(p => p.Id == profileId, ct)
                     ?? throw new ModelSkippedException("назначенный профиль LOI удалён");
        var profile = stored.ToProfile();
        if (RunValidator.RuleProblems(profile) is { Count: > 0 } problems)
            throw new ModelSkippedException($"профиль не готов к запуску: {string.Join(" ", problems)}");

        var history = parsed.HistoryFile.Trim();
        if (history.Length > 0)
        {
            var timeZone = (await services.GetRequiredService<ISettingsStore>().GetAsync(ct)).TimeZone;
            var scheduleName = await db.Schedules.Where(s => s.Id == context.ScheduleId).Select(s => s.Name).SingleOrDefaultAsync(ct) ?? "";
            history = OutputFolderTemplate.Render(history, context.RunStartedAt, timeZone, scheduleName);
        }

        var payload = new LoiJobPayload(model.Name, model.ModelPath, model.IsServer, context.RevitYear, context.OutputFolder, context.RunStartedAt,
            stored.ProfileJson, parsed.Resolve(profile, history));
        return JsonSerializer.Serialize(payload, ToadFlowDb.Json);
    }

    /// <summary>Проверенная модель — в аналитику; каталог, снятый Revit, — в таблицу каталогов, редактор правил видит его сразу.</summary>
    public Task OnJobCompletedAsync(Job job, IServiceProvider services, CancellationToken ct) => job.ModuleKind switch
    {
        ModuleKinds.Loi => RecordCheckAsync(job, services, ct),
        ModuleKinds.LoiCatalog => SaveCatalogAsync(job, services, ct),
        _ => Task.CompletedTask
    };

    /// <summary>Итог проверки — в аналитику, если её не выключили в расписании (выключена во всём сервисе — решит <see cref="CheckRecorder" />).</summary>
    private static async Task RecordCheckAsync(Job job, IServiceProvider services, CancellationToken ct)
    {
        if (job is not { Status: JobStatus.Succeeded or JobStatus.Warning, ResultJson: { } json }) return;
        if (JsonSerializer.Deserialize<CheckSummary>(json, ToadFlowDb.Json) is not { } summary) return;

        var db = services.GetRequiredService<ToadFlowDb>();
        var scheduleId = await db.Runs.Where(r => r.Id == job.RunId).Select(r => r.ScheduleId).SingleOrDefaultAsync(ct);
        var settings = scheduleId is null ? null : await db.Schedules.Where(s => s.Id == scheduleId).Select(s => s.ModuleSettingsJson).SingleOrDefaultAsync(ct);
        if (settings is not null && LoiScheduleSettings.TryParse(settings) is { Analytics: false }) return;

        if (await services.GetRequiredService<CheckRecorder>().RecordAsync(job.Id, summary, new CheckOrigin(LoiCheckSource.Schedule, job.RunId, scheduleId), ct))
            services.GetRequiredService<IDomainEvents>().Publish(new LoiCheckRecorded(Actor.System, job.Id));
    }

    private static async Task SaveCatalogAsync(Job job, IServiceProvider services, CancellationToken ct)
    {
        if (job is not { Status: JobStatus.Succeeded, ResultJson: { } json, RevitYear: { } year }) return;

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
