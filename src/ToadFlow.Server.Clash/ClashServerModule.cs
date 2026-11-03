using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ToadFlow.Contracts;
using ToadFlow.Contracts.Clash;
using Microsoft.AspNetCore.Authentication;
using ToadFlow.Server.Clash.Analytics;
using ToadFlow.Server.Clash.Collisions;
using ToadFlow.Server.Clash.Engine;
using ToadFlow.Server.Clash.Images;
using ToadFlow.Server.Clash.Overview;
using ToadFlow.Server.Clash.Parts;
using ToadFlow.Server.Clash.Plugin;
using ToadFlow.Server.Clash.Projects;
using ToadFlow.Server.Clash.Reports;
using ToadFlow.Server.Clash.Templates;
using ToadFlow.Server.Clash.Uploads;
using ToadFlow.Server.Clash.Viewer;
using ToadFlow.Server.Data;
using ToadFlow.Server.Data.Entities;
using ToadFlow.Server.Events;
using ToadFlow.Server.Modules;
using ToadFlow.Server.Naming;

namespace ToadFlow.Server.Clash;

/// <summary>
///     Модуль «Коллизии», этап выгрузки: задания агентам на выгрузку геометрии и параметров модели, приём слепков
///     и их хранение (последние N на модель), каталог категорий и параметров по слепкам.
/// </summary>
public sealed class ClashServerModule : IServerModule
{
    public string Kind => ModuleKinds.ClashExtract;

    /// <summary>Слепок уходит в хранилище сервера, а не в папку результатов.</summary>
    public bool UsesOutputFolder => false;

    public void AddServices(IServiceCollection services)
    {
        services.AddOptions<ClashStorageOptions>().Configure<IConfiguration, IHostEnvironment>((options, configuration, environment) =>
            options.Root = configuration["Clash:StorageRoot"] is { Length: > 0 } root
                ? root
                : environment.IsDevelopment()
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ToadFlow", "clash")
                    : "/data/clash");
        services.AddOptions<ClashEngineOptions>().BindConfiguration(ClashEngineOptions.Section).ValidateOnStart();
        services.AddSingleton<IValidateOptions<ClashEngineOptions>, ClashEngineOptionsValidator>();
        services.AddScoped<ClashStorage>();
        services.AddScoped<ClashSettingsStore>();
        services.AddScoped<SnapshotPublisher>();
        services.AddHostedService<ClashUploadCleaner>();
        services.AddScoped<PartModels>();
        services.AddScoped<ModelResolver>();
        services.AddScoped<ModelCollisions>();
        services.AddScoped<PartElements>();
        services.AddScoped<IDisciplineSource, PartDisciplines>();
        services.AddSingleton<PartElementsCache>();
        services.AddOptions<CollisionImageOptions>().Configure<IConfiguration>((options, configuration) =>
            options.Threads = configuration.GetValue<int>("Clash:ImageThreads"));
        services.AddSingleton<CollisionImages>();
        services.AddSingleton<PartPresence>();
        services.AddScoped<DraftHits>();
        services.AddScoped<ApprovedRuns>();
        services.AddScoped<CollisionReconciler>();
        services.AddScoped<DisciplineOrder>();
        services.AddScoped<RunReports>();
        services.AddSingleton<ReportArchives>();
        services.AddHostedService(provider => provider.GetRequiredService<ReportArchives>());
        services.AddHostedService<EngineLeaseSweeper>();
        services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, EngineAuthHandler>(EngineAuthHandler.SchemeName, null);
        services.AddAuthorization(options => options.AddPolicy(EngineAuthHandler.Policy,
            policy => policy.AddAuthenticationSchemes(EngineAuthHandler.SchemeName).RequireAuthenticatedUser()));
    }

    public void ConfigureModel(ModelBuilder model)
    {
        model.Entity<ClashModel>(entity =>
        {
            entity.ToTable("clash_models");
            entity.Property(m => m.Id).ValueGeneratedNever();
            entity.HasIndex(m => m.Key).IsUnique();
            entity.Ignore(m => m.SnapshotKey);
        });

        model.Entity<ClashSnapshot>(entity =>
        {
            entity.ToTable("clash_snapshots");
            entity.Property(s => s.Id).ValueGeneratedNever();
            entity.Property(s => s.Catalog).AsJson();
            entity.HasIndex(s => new { s.ModelId, s.ExtractedAt });
            entity.HasOne<ClashModel>().WithMany().HasForeignKey(s => s.ModelId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<ClashUpload>(entity =>
        {
            entity.ToTable("clash_uploads");
            entity.Property(u => u.Id).ValueGeneratedNever();
            entity.HasIndex(u => u.JobId);
            entity.HasIndex(u => u.CreatedAt);
        });

        model.Entity<ClashPart>(entity =>
        {
            entity.ToTable("clash_parts");
            entity.Property(p => p.Id).ValueGeneratedNever();
            entity.Property(p => p.DraftJson).HasColumnType("jsonb");
        });

        model.Entity<ClashDraftVersion>(entity =>
        {
            entity.ToTable("clash_draft_versions");
            entity.HasKey(v => new { v.PartId, v.Version });
            entity.Property(v => v.ConfigurationJson).HasColumnType("jsonb");
            entity.HasOne<ClashPart>().WithMany().HasForeignKey(v => v.PartId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<ClashPartVersion>(entity =>
        {
            entity.ToTable("clash_part_versions");
            entity.HasKey(v => new { v.PartId, v.Version });
            entity.Property(v => v.ConfigurationJson).HasColumnType("jsonb");
            entity.Property(v => v.ModelsJson).HasColumnType("jsonb");
            entity.HasOne<ClashPart>().WithMany().HasForeignKey(v => v.PartId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<ClashPartModel>(entity =>
        {
            entity.ToTable("clash_part_models");
            entity.HasKey(m => new { m.PartId, m.ModelId });
            entity.HasOne<ClashPart>().WithMany().HasForeignKey(m => m.PartId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<ClashModel>().WithMany().HasForeignKey(m => m.ModelId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<ClashTemplate>(entity =>
        {
            entity.ToTable("clash_templates");
            entity.Property(t => t.Id).ValueGeneratedNever();
            entity.Property(t => t.ConfigurationJson).HasColumnType("jsonb");
            entity.HasIndex(t => t.Name).IsUnique();
        });

        model.Entity<EngineRun>(entity =>
        {
            entity.ToTable("clash_engine_runs");
            entity.Property(r => r.Id).ValueGeneratedNever();
            entity.Property(r => r.ConfigurationJson).HasColumnType("jsonb");
            entity.Property(r => r.Models).AsJson();
            entity.Property(r => r.Stats).AsJson();
            entity.Property(r => r.Summary).AsJson();
            entity.HasIndex(r => new { r.State, r.CreatedAt });
            entity.HasIndex(r => new { r.PartId, r.CreatedAt });
            // Ждущий утверждённый прогон части — один: расписание и кнопка не ставят второй.
            entity.HasIndex(r => r.PartId).IsUnique().HasDatabaseName("ix_clash_engine_runs_one_queued_approved")
                .HasFilter("kind = 'Approved' AND state = 'Queued'");
            // Запуск расписания ставит прогон части один раз, сколько бы заданий ни закончилось одновременно.
            entity.HasIndex(r => new { r.PartId, r.TriggerRunId }).IsUnique().HasFilter("trigger_run_id IS NOT NULL");
            entity.HasOne<ClashPart>().WithMany().HasForeignKey(r => r.PartId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<ClashDraftHit>(entity =>
        {
            entity.ToTable("clash_draft_hits");
            entity.HasKey(h => new { h.RunId, h.Number });
            entity.HasIndex(h => new { h.PartId, h.DisciplinePair });
            entity.HasIndex(h => h.PartId);
            entity.HasIndex(h => h.Checks).HasMethod("gin");
            // Удалённая часть забирает находки своих черновиков: их бывают сотни тысяч.
            entity.HasOne<ClashPart>().WithMany().HasForeignKey(h => h.PartId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<ClashApprovedHit>(entity =>
        {
            entity.ToTable("clash_approved_hits");
            entity.HasKey(h => new { h.RunId, h.Number });
            entity.HasOne<EngineRun>().WithMany().HasForeignKey(h => h.RunId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<ClashCollision>(entity =>
        {
            entity.ToTable("clash_collisions");
            entity.Property(c => c.Id).ValueGeneratedNever();
            entity.HasIndex(c => new { c.PartId, c.ModelA, c.UniqueIdA, c.ModelB, c.UniqueIdB }).IsUnique();
            entity.HasIndex(c => new { c.PartId, c.Number }).IsUnique();
            entity.HasIndex(c => new { c.PartId, c.Status });
            entity.HasIndex(c => new { c.PartId, c.LastFoundRunId });
            // Плагин и «Мои» ищут коллизии модели: ModelA = @m OR ModelB = @m — по индексу на каждую сторону.
            entity.HasIndex(c => c.ModelA).HasDatabaseName("ix_clash_collisions_model_a");
            entity.HasIndex(c => c.ModelB).HasDatabaseName("ix_clash_collisions_model_b");
            entity.HasIndex(c => c.Checks).HasMethod("gin");
            entity.HasOne<ClashPart>().WithMany().HasForeignKey(c => c.PartId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<ClashCollisionSample>(entity =>
        {
            entity.ToTable("clash_collision_samples");
            entity.HasKey(s => new { s.CollisionId, s.RunId });
            entity.HasOne<ClashCollision>().WithMany().HasForeignKey(s => s.CollisionId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<ClashCollisionEvent>(entity =>
        {
            entity.ToTable("clash_collision_events");
            entity.Property(e => e.Id).ValueGeneratedNever();
            entity.HasIndex(e => new { e.CollisionId, e.At });
            entity.HasIndex(e => e.RunId).HasFilter("run_id IS NOT NULL");
            entity.HasOne<ClashCollision>().WithMany().HasForeignKey(e => e.CollisionId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<ClashCollisionSeen>(entity =>
        {
            entity.ToTable("clash_collision_seen");
            entity.HasKey(s => new { s.UserId, s.CollisionId });
            entity.HasIndex(s => s.CollisionId);
            entity.HasOne<User>().WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<ClashCollision>().WithMany().HasForeignKey(s => s.CollisionId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<ClashUserState>(entity =>
        {
            entity.ToTable("clash_user_states");
            entity.HasKey(s => s.UserId);
            entity.HasOne<User>().WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<ClashRunPairStat>(entity =>
        {
            entity.ToTable("clash_run_pair_stats");
            entity.HasKey(s => new { s.RunId, s.DisciplinePair });
            entity.HasIndex(s => s.PartId);
            entity.HasOne<ClashPart>().WithMany().HasForeignKey(s => s.PartId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<ClashCollisionStat>(entity =>
        {
            entity.ToTable("clash_collision_stats");
            entity.HasKey(s => new { s.RunId, s.Discipline });
            entity.HasIndex(s => new { s.PartId, s.Discipline, s.At });
            entity.HasOne<ClashPart>().WithMany().HasForeignKey(s => s.PartId).OnDelete(DeleteBehavior.Cascade);
        });
    }

    public void MapExternalEndpoints(IEndpointRouteBuilder app)
    {
        app.MapClashEngine();
    }

    public void MapEndpoints(RouteGroupBuilder api)
    {
        api.MapClash();
        api.MapClashParts();
        api.MapClashPartSchedules();
        api.MapClashDrafts();
        api.MapClashSetPreview();
        api.MapClashDraftRuns();
        api.MapClashDraftResults();
        api.MapClashTemplates();
        api.MapClashViewer();
        api.MapClashPresence();
        api.MapClashApprove();
        api.MapClashProjects();
        api.MapClashOverview();
    }

    /// <summary>Проектировщику — только коллизии утверждённых проверок и версии; настройка проверок остаётся в <see cref="MapEndpoints" />.</summary>
    public void MapMemberEndpoints(RouteGroupBuilder members)
    {
        members.MapClashCollisions();
        members.MapClashVersions();
        members.MapClashMy();
        members.MapClashPlugin();
        members.MapClashChecks();
        members.MapClashAnalytics();
        members.MapClashReports();
        members.MapClashReportArchives();
    }

    /// <summary>
    ///     Выгрузка закончилась: опубликованный слепок — в «События системы»; если это последнее задание запуска расписания,
    ///     утверждённые части из настроек расписания встают в очередь движка.
    /// </summary>
    public async Task OnJobCompletedAsync(Job job, IServiceProvider services, CancellationToken ct)
    {
        if (job.ModuleKind != ModuleKinds.ClashExtract) return;
        await ReportSnapshotAsync(job, services, ct);
        await ScheduleSettledAsync(job, services, ct);
    }

    /// <summary>
    ///     Все задания запуска закончились: по прогону на каждую утверждённую часть расписания. Модели части, чьё задание в
    ///     запуске не удалось, — «не проверены». Отменённый запуск прогонов не ставит. Повторный вызов того же запуска второй
    ///     прогон не добавит: прогон на запуск у части один.
    /// </summary>
    private static async Task ScheduleSettledAsync(Job job, IServiceProvider services, CancellationToken ct)
    {
        var db = services.GetRequiredService<ToadFlowDb>();
        var run = await db.Runs.AsNoTracking().SingleOrDefaultAsync(r => r.Id == job.RunId, ct);
        if (run is not { ScheduleId: { } scheduleId, CancelRequested: false }) return;
        var jobs = await db.Jobs.AsNoTracking().Where(j => j.RunId == run.Id).Select(j => new { j.Status, j.PayloadJson }).ToListAsync(ct);
        if (jobs.Any(j => !Job.IsFinal(j.Status))) return;

        var settingsJson = await db.Schedules.Where(s => s.Id == scheduleId).Select(s => s.ModuleSettingsJson).SingleOrDefaultAsync(ct);
        if (settingsJson is null) return;
        using var document = JsonDocument.Parse(settingsJson);
        if (ClashScheduleSettings.TryParse(document.RootElement) is not { Parts: { Count: > 0 } parts }) return;

        var failed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var other in jobs.Where(j => j.Status is not (JobStatus.Succeeded or JobStatus.Warning)))
        {
            try
            {
                if (JsonSerializer.Deserialize<ClashExtractPayload>(other.PayloadJson, ToadFlowDb.Json)?.ModelKey is { } key) failed.Add(key);
            }
            catch (JsonException)
            {
            }
        }

        var approvedRuns = services.GetRequiredService<ApprovedRuns>();
        foreach (var partId in parts)
        {
            var partModels = await approvedRuns.ModelsAsync(partId, ct);
            var unchecked_ = partModels.Where(m => failed.Contains(m.ModelId.ToString("N"))).Select(m => m.ModelId).ToList();
            await approvedRuns.EnqueueAsync(partId, run.Id, unchecked_, Actor.System, ct);
        }
    }

    private static async Task ReportSnapshotAsync(Job job, IServiceProvider services, CancellationToken ct)
    {
        if (job is not { Status: JobStatus.Succeeded or JobStatus.Warning, ResultJson: { } json }) return;

        ClashExtractResult? result;
        try
        {
            result = JsonSerializer.Deserialize<ClashExtractResult>(json, ToadFlowDb.Json);
        }
        catch (JsonException)
        {
            result = null;
        }

        var db = services.GetRequiredService<ToadFlowDb>();
        var snapshot = result is null
            ? null
            : await db.Set<ClashSnapshot>().AsNoTracking().SingleOrDefaultAsync(s => s.Id == result.SnapshotId && s.JobId == job.Id, ct);
        if (snapshot is null)
        {
            services.GetRequiredService<ILogger<ClashServerModule>>()
                .LogWarning("Выгрузка {Job} ({Model}) сдана без опубликованного слепка", job.Id, job.ModelName);
            return;
        }

        services.GetRequiredService<IDomainEvents>().Publish(new CatalogChanged(Actor.System, ClashEndpoints.ModelsEntity, snapshot.ModelId,
            $"Выгружена модель {job.ModelName}: элементов {snapshot.Elements}, треугольников {snapshot.Triangles}"));
    }

    public void MapAgentEndpoints(RouteGroupBuilder agentApi) => agentApi.MapClashUploads();

    public async Task<IReadOnlyList<string>> ValidateSettingsAsync(JsonElement settings, IServiceProvider services, CancellationToken ct)
    {
        var parsed = ClashScheduleSettings.TryParse(settings);
        if (parsed is null) return ["Не заданы параметры выгрузки для коллизий"];
        if (parsed.ViewName.Length == 0) return ["Укажите имя 3D-вида для выгрузки"];
        if (parsed.Parts is not { Count: > 0 } ids) return [];

        var known = await services.GetRequiredService<ToadFlowDb>().Set<ClashPart>().AsNoTracking().Where(p => ids.Contains(p.Id))
            .Select(p => new { p.Id, p.Name, p.ApprovedVersion }).ToListAsync(ct);
        var errors = new List<string>();
        if (ids.Any(id => known.All(p => p.Id != id))) errors.Add("Часть не найдена — уберите её из расписания");
        errors.AddRange(known.Where(p => p.ApprovedVersion is null).Select(p => $"Часть {p.Name} не утверждена — расписание прогоняет только утверждённые"));
        return errors;
    }

    public async Task<string> BuildPayloadAsync(JobBuildContext context, IServiceProvider services, CancellationToken ct)
    {
        var settings = ClashScheduleSettings.TryParse(context.ModuleSettings)
                       ?? throw new InvalidOperationException("Не заданы параметры выгрузки для коллизий");
        var model = context.Model;
        var stored = await services.GetRequiredService<ClashStorage>().EnsureModelAsync(model.Name, model.ModelPath, ct);
        var payload = new ClashExtractPayload(model.Name, model.ModelPath, model.IsServer, context.RevitYear, stored.SnapshotKey, settings.ViewName,
            settings.WorksetKeyword);
        return JsonSerializer.Serialize(payload, ToadFlowDb.Json);
    }
}
