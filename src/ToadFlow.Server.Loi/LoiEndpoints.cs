using System.Text;
using System.Text.Json;
using Alexpivo394.Revit.ParamCheck.Profiles;
using Microsoft.EntityFrameworkCore;
using ToadFlow.Contracts;
using ToadFlow.Contracts.Loi;
using ToadFlow.Server.Auth;
using ToadFlow.Server.Data;
using ToadFlow.Server.Events;
using ToadFlow.Server.Loi.Analytics;
using ToadFlow.Server.Loi.Naming;

namespace ToadFlow.Server.Loi;

/// <param name="Problems">Чего не хватает профилю для запуска; расписание с таким профилем не сохранится.</param>
public sealed record LoiProfileSummary(
    Guid Id,
    string Name,
    int RuleCount,
    string ViewName,
    string WorksetKeyword,
    DateTimeOffset UpdatedAt,
    string UpdatedBy,
    IReadOnlyList<string> Problems);

/// <param name="Profile">Профиль в формате ParamChecker — как в файлах профилей плагина (имена полей с заглавной буквы).</param>
public sealed record LoiProfileDetails(Guid Id, string Name, DateTimeOffset UpdatedAt, string UpdatedBy, JsonElement Profile, IReadOnlyList<string> Problems);

public sealed record LoiProfileRequest(JsonElement Profile);

/// <param name="Json">Содержимое файла профилей ParamChecker (экспорт или profiles.json) любой версии.</param>
public sealed record LoiImportRequest(string Json);

/// <param name="Warnings">Что из файла не перенеслось — например, категории старого формата, которых нет в каталогах.</param>
public sealed record LoiImportResult(IReadOnlyList<LoiProfileSummary> Created, IReadOnlyList<string> Warnings);

public sealed record LoiCatalogSummary(int RevitYear, DateTimeOffset UpdatedAt, int Categories, int Parameters, bool HasSharedParameterFile);

/// <param name="AgentYears">Версии Revit, которые есть у агентов, — из них можно снять каталог.</param>
public sealed record LoiCatalogOverview(IReadOnlyList<LoiCatalogSummary> Catalogs, IReadOnlyList<int> AgentYears);

public sealed record LoiCatalogView(int RevitYear, DateTimeOffset UpdatedAt, LoiCatalogResult Catalog);

public sealed record LoiRefreshRequest(int? RevitYear);

/// <param name="KnownDisciplines">Разделы из справочника именования и записанных проверок — подсказки для назначения.</param>
public sealed record LoiRoutingView(IReadOnlyList<DisciplineProfile> Disciplines, Guid? FallbackProfileId, IReadOnlyList<string> KnownDisciplines);

public sealed record LoiModelRef(string Name, string ModelPath);

/// <param name="Settings">Настройки расписания, как в форме, — ещё не сохранённые.</param>
/// <param name="ScheduleId">Сохранённое расписание: к <paramref name="Models" /> добавляются модели его последнего запуска.</param>
/// <param name="Models">Модели, отмеченные в расписании явно.</param>
public sealed record LoiRoutingPreviewRequest(JsonElement Settings, Guid? ScheduleId, IReadOnlyList<LoiModelRef> Models);

public sealed record LoiRoutingPreviewRow(string Name, string ModelPath, string? Discipline, Guid? ProfileId, string? ProfileName, ProfileVia Via);

internal static class LoiEndpoints
{
    /// <summary>Сущность живых обновлений интерфейса для каталога (профили — <see cref="ProfilesEntity" />).</summary>
    public const string CatalogEntity = "loi-catalog";

    public const string ProfilesEntity = "loi-profiles";

    public const string RoutingEntity = "loi-routing";

    public static void MapLoi(this RouteGroupBuilder api)
    {
        var loi = api.MapGroup("/loi");
        loi.MapLoiAnalyticsAdmin();
        loi.MapLoiAnalyticsQueries();
        loi.MapGet("/profiles", async (ToadFlowDb db) => await SummariesAsync(db, null));
        loi.MapGet("/profiles/{id:guid}", GetProfileAsync);
        loi.MapGet("/profiles/{id:guid}/file", DownloadAsync);
        loi.MapGet("/catalog", CatalogOverviewAsync);
        loi.MapGet("/catalog/{year:int}", async (int year, ToadFlowDb db) =>
            await db.Set<LoiCatalog>().AsNoTracking().SingleOrDefaultAsync(c => c.RevitYear == year) is { } catalog
                ? Results.Ok(new LoiCatalogView(catalog.RevitYear, catalog.UpdatedAt, catalog.Catalog))
                : Results.NotFound());

        var write = loi.MapGroup("").RequireAuthorization(Policies.Operator);
        write.MapPost("/profiles", (LoiProfileRequest request, ToadFlowDb db, HttpContext http, IDomainEvents events, TimeProvider time) =>
            SaveAsync(null, request, db, http, events, time));
        write.MapPut("/profiles/{id:guid}", (Guid id, LoiProfileRequest request, ToadFlowDb db, HttpContext http, IDomainEvents events, TimeProvider time) =>
            SaveAsync(id, request, db, http, events, time));
        write.MapDelete("/profiles/{id:guid}", DeleteAsync);
        write.MapPost("/profiles/import", ImportAsync);
        loi.MapGet("/routing", RoutingAsync);
        loi.MapPost("/routing/preview", PreviewRoutingAsync);
        write.MapPut("/routing", SaveRoutingAsync);
        write.MapPost("/catalog/refresh", async (LoiRefreshRequest request, LoiCatalogRefresher refresher, HttpContext http) =>
        {
            try
            {
                var run = await refresher.StartAsync(request.RevitYear, http.User.UserId(), http.User.Actor());
                return Results.Ok(new { runId = run.Id });
            }
            catch (CatalogUnavailableException exception)
            {
                return Results.Conflict(new { error = exception.Message });
            }
        });
    }

    private static async Task<IResult> GetProfileAsync(Guid id, ToadFlowDb db)
    {
        var stored = await db.Set<LoiProfile>().AsNoTracking().SingleOrDefaultAsync(p => p.Id == id);
        if (stored is null) return Results.NotFound();

        var profile = stored.ToProfile();
        using var json = JsonDocument.Parse(stored.ProfileJson);
        return Results.Ok(new LoiProfileDetails(stored.Id, stored.Name, stored.UpdatedAt, await UserNameAsync(db, stored.UpdatedByUserId),
            json.RootElement.Clone(), RunValidator.RuleProblems(profile)));
    }

    /// <summary>Файл профилей с одним профилем — плагин ParamChecker импортирует его как свой.</summary>
    private static async Task<IResult> DownloadAsync(Guid id, ToadFlowDb db)
    {
        var stored = await db.Set<LoiProfile>().AsNoTracking().SingleOrDefaultAsync(p => p.Id == id);
        if (stored is null) return Results.NotFound();

        var text = ProfileSerializer.Serialize(new ProfileSet { Profiles = [stored.ToProfile()] });
        return Results.File(Encoding.UTF8.GetBytes(text), "application/json; charset=utf-8", $"{FileName(stored.Name)}.json");
    }

    private static async Task<IResult> SaveAsync(Guid? id, LoiProfileRequest request, ToadFlowDb db, HttpContext http, IDomainEvents events, TimeProvider time)
    {
        CheckProfile profile;
        try
        {
            profile = ProfileSerializer.DeserializeProfile(request.Profile.GetRawText());
        }
        catch (InvalidDataException exception)
        {
            return Results.BadRequest(new { error = $"Профиль не прочитан: {exception.Message}" });
        }

        profile.Name = profile.Name.Trim();
        if (profile.Name.Length == 0) return Results.BadRequest(new { error = "Укажите название профиля" });
        if (await db.Set<LoiProfile>().AnyAsync(p => p.Name == profile.Name && p.Id != id))
            return Results.Conflict(new { error = $"Профиль «{profile.Name}» уже есть" });

        var stored = id is null ? null : await db.Set<LoiProfile>().FindAsync(id);
        if (id is not null && stored is null) return Results.NotFound();
        if (stored is null) db.Set<LoiProfile>().Add(stored = new LoiProfile { Id = Guid.CreateVersion7() });

        Apply(stored, profile, http.User.UserId(), time.GetUtcNow());
        await db.SaveChangesAsync();
        events.Publish(new CatalogChanged(http.User.Actor(), ProfilesEntity, stored.Id,
            $"{(id is null ? "Добавлен" : "Изменён")} профиль проверки LOI «{stored.Name}»"));

        var summary = (await SummariesAsync(db, stored.Id)).Single();
        return id is null ? Results.Created($"/api/loi/profiles/{stored.Id}", summary) : Results.Ok(summary);
    }

    private static async Task<IResult> DeleteAsync(Guid id, ToadFlowDb db, LoiSettingsStore settings, HttpContext http, IDomainEvents events)
    {
        var stored = await db.Set<LoiProfile>().FindAsync(id);
        if (stored is null) return Results.NotFound();

        if (await LoiProfileResolver.IsUsedAsync(db, settings, id))
            return Results.Conflict(new { error = "Профиль используется в расписаниях или назначен разделу — сначала поменяйте его там" });

        db.Set<LoiProfile>().Remove(stored);
        await db.SaveChangesAsync();
        events.Publish(new CatalogChanged(http.User.Actor(), ProfilesEntity, id, $"Удалён профиль проверки LOI «{stored.Name}»"));
        return Results.NoContent();
    }

    /// <summary>Профили из файла ParamChecker добавляются к имеющимся; одноимённые получают номер — ничего не перезаписывается.</summary>
    private static async Task<IResult> ImportAsync(LoiImportRequest request, ToadFlowDb db, HttpContext http, IDomainEvents events, TimeProvider time)
    {
        var unknown = new SortedSet<int>();
        ProfileSet set;
        try
        {
            set = ProfileSerializer.Deserialize(request.Json ?? "", id =>
            {
                var name = BuiltInCategories.Name(id);
                if (name is null) unknown.Add(id);
                return name;
            });
        }
        catch (InvalidDataException exception)
        {
            return Results.BadRequest(new { error = $"Файл не прочитан: {exception.Message}" });
        }

        if (set.Profiles.Count == 0) return Results.BadRequest(new { error = "В файле нет профилей" });

        var names = new HashSet<string>(await db.Set<LoiProfile>().Select(p => p.Name).ToListAsync());
        var userId = http.User.UserId();
        var now = time.GetUtcNow();
        var created = new List<LoiProfile>();
        foreach (var profile in set.Profiles)
        {
            profile.Name = UniqueName(names, profile.Name.Trim().Length == 0 ? "Профиль" : profile.Name.Trim());
            var stored = new LoiProfile { Id = Guid.CreateVersion7() };
            Apply(stored, profile, userId, now);
            db.Set<LoiProfile>().Add(stored);
            created.Add(stored);
        }

        await db.SaveChangesAsync();
        events.Publish(new CatalogChanged(http.User.Actor(), ProfilesEntity, created[0].Id,
            $"Загружены профили проверки LOI из файла ParamChecker: {string.Join(", ", created.Select(p => $"«{p.Name}»"))}"));

        IReadOnlyList<string> warnings = unknown.Count == 0 ? [] : [$"Неизвестные номера категорий не перенесены: {string.Join(", ", unknown)}"];

        var ids = created.Select(p => p.Id).ToHashSet();
        return Results.Ok(new LoiImportResult((await SummariesAsync(db, null)).Where(s => ids.Contains(s.Id)).ToList(), warnings));
    }

    private static async Task<LoiRoutingView> RoutingAsync(LoiSettingsStore settings, ToadFlowDb db, CancellationToken ct)
    {
        var routing = await settings.RoutingAsync(ct);
        // Разделы для подсказок: из справочника именования и из уже записанных проверок.
        var naming = (await settings.NamingAsync(ct)).Entries.Where(e => e.Field == NamingFields.Discipline).Select(e => e.Value);
        var checkedModels = await db.Set<LoiCheck>().Where(c => c.Discipline != null).Select(c => c.Discipline!).Distinct().ToListAsync(ct);
        var known = naming.Concat(checkedModels).Where(d => !string.IsNullOrWhiteSpace(d)).Select(d => d.Trim()).Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.CurrentCulture).ToList();
        return new LoiRoutingView(routing.Disciplines, routing.FallbackProfileId, known);
    }

    private static async Task<IResult> SaveRoutingAsync(LoiProfileRouting request, LoiSettingsStore settings, ToadFlowDb db, HttpContext http, IDomainEvents events,
        CancellationToken ct)
    {
        var routing = request with
        {
            Disciplines = (request.Disciplines ?? []).Select(d => d with { Discipline = d.Discipline?.Trim() ?? "" }).ToList()
        };
        var errors = new List<string>();
        if (routing.Disciplines.Any(d => d.Discipline.Length == 0)) errors.Add("Укажите раздел в каждой строке");
        var duplicates = routing.Disciplines.GroupBy(d => d.Discipline, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1 && g.Key.Length > 0)
            .Select(g => g.Key).ToList();
        if (duplicates.Count > 0) errors.Add($"Раздел назначен дважды: {string.Join(", ", duplicates)}");
        var ids = routing.ProfileIds.Distinct().ToList();
        if (await db.Set<LoiProfile>().CountAsync(p => ids.Contains(p.Id), ct) < ids.Count) errors.Add("Профиль не найден — обновите страницу");
        if (errors.Count > 0) return Results.BadRequest(new { error = string.Join("; ", errors) });

        await settings.SaveRoutingAsync(routing, ct);
        events.Publish(new CatalogChanged(http.User.Actor(), RoutingEntity, Guid.Empty,
            $"Профили LOI по разделам: назначено разделов {routing.Disciplines.Count}, остальные модели — {(routing.FallbackProfileId is null ? "пропускаются" : "общим профилем")}"));
        return Results.Ok(routing);
    }

    /// <summary>Какой профиль получит каждая модель расписания — с ещё не сохранёнными настройками.</summary>
    private static async Task<IReadOnlyList<LoiRoutingPreviewRow>> PreviewRoutingAsync(LoiRoutingPreviewRequest request, LoiProfileResolver resolver, ToadFlowDb db,
        CancellationToken ct)
    {
        var settings = LoiScheduleSettings.TryParse(request.Settings) ?? new LoiScheduleSettings(null);
        var models = request.Models.ToList();
        if (request.ScheduleId is { } scheduleId &&
            await db.Schedules.AsNoTracking().Where(s => s.Id == scheduleId).Select(s => s.LastKnownModels).SingleOrDefaultAsync(ct) is { } known)
            models.AddRange(known.Select(m => new LoiModelRef(m.Name, m.ModelPath)));

        var names = await db.Set<LoiProfile>().AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.Name, ct);
        var rows = new List<LoiRoutingPreviewRow>();
        foreach (var model in models.DistinctBy(m => m.ModelPath.Length > 0 ? m.ModelPath : m.Name, StringComparer.OrdinalIgnoreCase).OrderBy(m => m.Name))
        {
            var choice = await resolver.ChooseAsync(settings, model.Name, model.ModelPath, ct);
            var profile = choice.ProfileId is { } id ? names.GetValueOrDefault(id) : null;
            rows.Add(new LoiRoutingPreviewRow(model.Name, model.ModelPath, choice.Discipline, choice.ProfileId, profile, choice.Via));
        }

        return rows;
    }

    private static async Task<LoiCatalogOverview> CatalogOverviewAsync(ToadFlowDb db)
    {
        var catalogs = await db.Set<LoiCatalog>().AsNoTracking().OrderByDescending(c => c.RevitYear).ToListAsync();
        var years = await LoiCatalogRefresher.YearsAsync(db);
        return new LoiCatalogOverview(
            catalogs.Select(c => new LoiCatalogSummary(c.RevitYear, c.UpdatedAt, c.Catalog.Categories.Length, c.Catalog.Parameters.Length,
                c.Catalog.HasSharedParameterFile)).ToList(),
            years);
    }

    /// <summary>Модели профиля в сервисе не нужны — их задаёт расписание; включённость тоже не имеет смысла.</summary>
    private static void Apply(LoiProfile stored, CheckProfile profile, Guid userId, DateTimeOffset now)
    {
        profile.Models = [];
        profile.IsEnabled = true;
        stored.Name = profile.Name;
        stored.ProfileJson = ProfileSerializer.SerializeProfile(profile);
        stored.UpdatedByUserId = userId;
        stored.UpdatedAt = now;
    }

    private static async Task<List<LoiProfileSummary>> SummariesAsync(ToadFlowDb db, Guid? id)
    {
        var query = db.Set<LoiProfile>().AsNoTracking();
        if (id is not null) query = query.Where(p => p.Id == id);
        var stored = await query.OrderBy(p => p.Name).ToListAsync();
        var users = await db.Users.AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.DisplayName);

        return stored.Select(p =>
        {
            var profile = p.ToProfile();
            return new LoiProfileSummary(p.Id, p.Name, profile.Rules.Count, profile.ViewName, profile.WorksetKeyword, p.UpdatedAt,
                users.GetValueOrDefault(p.UpdatedByUserId, "—"), RunValidator.RuleProblems(profile));
        }).ToList();
    }

    private static async Task<string> UserNameAsync(ToadFlowDb db, Guid userId) =>
        await db.Users.Where(u => u.Id == userId).Select(u => u.DisplayName).SingleOrDefaultAsync() ?? "—";

    private static string UniqueName(HashSet<string> taken, string name)
    {
        var candidate = name;
        for (var n = 2; !taken.Add(candidate); n++) candidate = $"{name} ({n})";
        return candidate;
    }

    private static string FileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(['<', '>', ':', '"', '/', '\\', '|', '?', '*']).ToHashSet();
        var safe = new string(name.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim();
        return safe.Length == 0 ? "profile" : safe;
    }
}
