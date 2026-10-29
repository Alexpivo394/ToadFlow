using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ToadFlow.Contracts;
using ToadFlow.Contracts.Clash;
using Microsoft.AspNetCore.Authentication;
using ToadFlow.Server.Clash.Engine;
using ToadFlow.Server.Clash.Parts;
using ToadFlow.Server.Clash.Projects;
using ToadFlow.Server.Clash.Templates;
using ToadFlow.Server.Clash.Uploads;
using ToadFlow.Server.Clash.Viewer;
using ToadFlow.Server.Data;
using ToadFlow.Server.Data.Entities;
using ToadFlow.Server.Events;
using ToadFlow.Server.Modules;

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
        services.AddScoped<ClashStorage>();
        services.AddScoped<ClashSettingsStore>();
        services.AddScoped<SnapshotPublisher>();
        services.AddHostedService<ClashUploadCleaner>();
        services.AddScoped<PartModels>();
        services.AddScoped<PartElements>();
        services.AddSingleton<PartElementsCache>();
        services.AddSingleton<PartPresence>();
        services.AddScoped<DraftHits>();
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
            entity.HasIndex(r => new { r.State, r.CreatedAt });
            entity.HasIndex(r => new { r.PartId, r.CreatedAt });
            entity.HasOne<ClashPart>().WithMany().HasForeignKey(r => r.PartId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<ClashDraftHit>(entity =>
        {
            entity.ToTable("clash_draft_hits");
            entity.HasKey(h => new { h.RunId, h.Number });
            entity.HasIndex(h => new { h.PartId, h.DisciplinePair });
            entity.HasIndex(h => h.PartId);
            entity.HasIndex(h => h.Checks).HasMethod("gin");
        });
    }

    public void MapExternalEndpoints(IEndpointRouteBuilder app) => app.MapClashEngine();

    public void MapEndpoints(RouteGroupBuilder api)
    {
        api.MapClash();
        api.MapClashParts();
        api.MapClashDrafts();
        api.MapClashSetPreview();
        api.MapClashDraftRuns();
        api.MapClashDraftResults();
        api.MapClashTemplates();
        api.MapClashViewer();
        api.MapClashPresence();
        api.MapClashApprove();
        api.MapClashProjects();
    }

    /// <summary>Выгрузка закончилась и слепок опубликован — в «События системы».</summary>
    public async Task OnJobCompletedAsync(Job job, IServiceProvider services, CancellationToken ct)
    {
        if (job is not { ModuleKind: ModuleKinds.ClashExtract, Status: JobStatus.Succeeded or JobStatus.Warning, ResultJson: { } json }) return;

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

    public Task<IReadOnlyList<string>> ValidateSettingsAsync(JsonElement settings, IServiceProvider services, CancellationToken ct)
    {
        IReadOnlyList<string> errors = ClashScheduleSettings.TryParse(settings) switch
        {
            null => ["Не заданы параметры выгрузки для коллизий"],
            { ViewName.Length: 0 } => ["Укажите имя 3D-вида для выгрузки"],
            _ => []
        };
        return Task.FromResult(errors);
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
