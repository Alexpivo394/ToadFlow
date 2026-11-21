using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ToadFlow.Contracts;
using ToadFlow.Server.Data.Entities;
using ToadFlow.Server.Families.Cards;
using ToadFlow.Server.Families.Data;
using ToadFlow.Server.Families.Read;
using ToadFlow.Server.Families.Scan;
using ToadFlow.Server.Families.Search;
using ToadFlow.Server.Modules;

namespace ToadFlow.Server.Families;

/// <summary>Корень хранилища модуля (том Docker <c>families</c>): картинки семейств. <c>Families:StorageRoot</c> в конфигурации.</summary>
public sealed class FamiliesStorageOptions
{
    public string Root { get; set; } = "";
}

/// <summary>
///     Модуль «Семейства»: каталог семейств Revit из сетевой папки библиотеки. Файлы лежат в папке, сервер их не видит: обходит
///     папку агент библиотеки, а содержимое семейства читает ферма заданием <see cref="ModuleKinds.FamiliesRead" />. Расписаний нет.
/// </summary>
public sealed class FamiliesServerModule : IServerModule
{
    public string Name => "families";

    public string Title => "Семейства";

    public string Kind => ModuleKinds.FamiliesRead;

    /// <summary>Истории запусков у модуля нет: непрочитанные файлы разбираются в «Нужно решение».</summary>
    public string HistoryPath => "/families/issues";

    public bool UsesOutputFolder => false;

    public void AddServices(IServiceCollection services)
    {
        services.AddOptions<FamiliesStorageOptions>().Configure<IConfiguration, IHostEnvironment>((options, configuration, environment) =>
            options.Root = configuration["Families:StorageRoot"] is { Length: > 0 } root
                ? root
                : environment.IsDevelopment()
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ToadFlow", "families")
                    : "/data/families");
        services.AddScoped<FamiliesSettingsStore>();
        services.AddSingleton<LibraryScanGate>();
        services.AddScoped<LibraryScanner>();
        services.AddScoped<ScanApplier>();
        services.AddHostedService<LibraryScanLoop>();
        services.AddSingleton<PreviewStore>();
        services.AddScoped<FamilyReadJobs>();
        services.AddHostedService<FamilyReadLoop>();
        services.AddSingleton<ToadFlow.Server.Overview.IDataVolume>(provider => new ToadFlow.Server.Overview.PathVolume("Картинки семейств",
            () => provider.GetRequiredService<IOptions<FamiliesStorageOptions>>().Value.Root));
    }

    public void ConfigureModel(ModelBuilder model)
    {
        model.Entity<FamilyCard>(card =>
        {
            card.ToTable("family_cards");
            card.Property(c => c.Id).ValueGeneratedNever();
            card.Property(c => c.Name).HasMaxLength(260);
            card.Property(c => c.NameKey).HasMaxLength(260);
            // Имя файла уникально на всю библиотеку: Revit различает семейства в проекте по имени. Архивные карточки не мешают.
            card.HasIndex(c => c.NameKey).IsUnique().HasFilter("state <> 'Archived'");
            card.HasIndex(c => new { c.State, c.UpdatedAt });
            card.Property(c => c.Tags).HasDefaultValueSql("'{}'::text[]");
        });

        model.Entity<FamilyVersion>(version =>
        {
            version.ToTable("family_versions");
            version.Property(v => v.Id).ValueGeneratedNever();
            version.Property(v => v.Hash).HasMaxLength(64);
            version.Property(v => v.CatalogHash).HasMaxLength(64);
            version.Property(v => v.ContentJson).HasColumnType("jsonb");
            version.HasIndex(v => new { v.CardId, v.SeenAt });
            version.HasIndex(v => v.Hash);
            version.HasOne<FamilyCard>().WithMany().HasForeignKey(v => v.CardId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<FamilyIssue>(issue =>
        {
            issue.ToTable("family_issues");
            issue.Property(i => i.Id).ValueGeneratedNever();
            issue.Property(i => i.Hash).HasMaxLength(64);
            issue.HasIndex(i => i.ResolvedAt);
        });

        model.Entity<FamilyScan>(scan =>
        {
            scan.ToTable("family_scans");
            scan.Property(s => s.Id).ValueGeneratedNever();
            scan.Property(s => s.Errors).HasDefaultValueSql("'{}'::text[]");
            scan.HasIndex(s => s.StartedAt);
        });
    }

    public void MapEndpoints(RouteGroupBuilder api)
    {
        var families = api.MapGroup("/families");
        families.MapFamiliesSettings();
        families.MapFamiliesScan();
        families.MapFamiliesReview();
    }

    /// <summary>Поиск и карточка — и проектировщику.</summary>
    public void MapMemberEndpoints(RouteGroupBuilder members)
    {
        var families = members.MapGroup("/families");
        families.MapFamiliesSearch();
        families.MapFamiliesCards();
    }

    public void MapAgentEndpoints(RouteGroupBuilder agentApi) => agentApi.MapFamiliesAgent();

    public Task<IReadOnlyList<string>> ValidateSettingsAsync(JsonElement settings, IServiceProvider services, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>(["У модуля «Семейства» нет расписаний"]);

    public Task<string> BuildPayloadAsync(JobBuildContext context, IServiceProvider services, CancellationToken ct) =>
        throw new NotSupportedException("У модуля «Семейства» нет расписаний: задания чтения создаёт сам модуль");

    /// <summary>Итог чтения семейства фермой — в версию и карточку.</summary>
    public Task OnJobCompletedAsync(Job job, IServiceProvider services, CancellationToken ct) =>
        job.ModuleKind == ModuleKinds.FamiliesRead ? services.GetRequiredService<FamilyReadJobs>().CompleteAsync(job, ct) : Task.CompletedTask;
}
