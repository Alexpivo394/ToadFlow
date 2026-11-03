using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Contracts.Clash;
using ToadFlow.Server.Clash;
using ToadFlow.Server.Clash.Collisions;
using ToadFlow.Server.Clash.Engine;
using ToadFlow.Server.Clash.Parts;
using ToadFlow.Server.Events;
using ToadFlow.Server.Tests.Infrastructure;
using static ToadFlow.Server.Tests.Infrastructure.ClashDraftFixtures;

namespace ToadFlow.Server.Tests;

/// <summary>Утверждённая версия хранит состав моделей части и их разделы: прогоны версии не зависят от правок состава после утверждения.</summary>
public class VersionCompositionTests : ServerTest
{
    private const string Ar = "POL_K1_AR.rvt";
    private const string Ov = "POL_K1_OV.rvt";
    private const string Vk = "POL_K1_VK.rvt";

    private async Task<(HttpClient Admin, FakeAgent Agent, Guid PartId)> SetupAsync(params string[] extra)
    {
        var admin = await AdminAsync();
        var agent = await FakeAgent.RegisterAsync(Server, admin);
        await agent.ConnectAsync("1.8.0", new InstalledRevitDto(2022, true));
        var partId = await PartAsync(Server, admin, agent, Ar, Ov);
        foreach (var model in extra)
        {
            var published = await ClashFixtures.PublishAsync(Server, agent, model);
            await agent.CompleteJobAsync(published.JobId, JobOutcome.Succeeded, null, System.Text.Json.JsonSerializer.Serialize(published.Result, TestJson.Options));
        }

        return (admin, agent, partId);
    }

    private static async Task<int> ApproveAsync(HttpClient admin, Guid partId)
    {
        var response = await admin.PostJsonAsync($"/api/clash/parts/{partId}/approve", new { comment = (string?)null, run = false });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.ReadJsonAsync<ApprovedVersionView>()).Version;
    }

    /// <summary>Состав части: модели по имени с поправкой раздела (null — по разбору имени).</summary>
    private static async Task SetModelsAsync(HttpClient admin, Guid partId, params (string Name, string? Discipline)[] models)
    {
        var known = await admin.GetJsonAsync<ClashModelView[]>("/api/clash/models");
        var body = models.Select(m => new { modelId = known.Single(k => k.Name == m.Name).Id, disciplineOverride = m.Discipline }).ToArray();
        (await admin.PutJsonAsync($"/api/clash/parts/{partId}/models", body)).EnsureSuccessStatusCode();
    }

    private async Task<EngineRun> RerunAsync(Guid partId)
    {
        using var scope = Server.Services.CreateScope();
        var run = await scope.ServiceProvider.GetRequiredService<ApprovedRuns>().EnqueueAsync(partId, null, [], Actor.System, CancellationToken.None);
        return run.ShouldNotBeNull();
    }

    private static string[] Composition(EngineRun run) => run.Models.Select(m => $"{m.ModelName}={m.Discipline}").Order().ToArray();

    [Test]
    public async Task Approve_SnapshotsModelsAndDisciplines()
    {
        var (admin, agent, partId) = await SetupAsync();
        await using var _ = agent;

        var version = await ApproveAsync(admin, partId);

        var stored = await DbAsync(db => db.Set<ClashPartVersion>().AsNoTracking().SingleAsync(v => v.PartId == partId && v.Version == version));
        VersionModels.Parse(stored.ModelsJson).ShouldNotBeNull().Select(m => $"{m.ModelName}={m.Discipline}").Order()
            .ShouldBe([$"{Ar}=АР", $"{Ov}=ОВ"]);
    }

    [Test]
    public async Task Approved_DisciplineChangedAfterApprove_RunUsesVersionComposition()
    {
        var (admin, agent, partId) = await SetupAsync();
        await using var _ = agent;
        await ApproveAsync(admin, partId);

        await SetModelsAsync(admin, partId, (Ar, null), (Ov, "ВК"));
        var run = await RerunAsync(partId);

        Composition(run).ShouldBe([$"{Ar}=АР", $"{Ov}=ОВ"]);
        (await admin.GetJsonAsync<ClashPartView>($"/api/clash/parts/{partId}")).CompositionPending.ShouldBeTrue();
    }

    [Test]
    public async Task Approved_ModelAddedToPartAfterApprove_NotInRunUntilApproved()
    {
        var (admin, agent, partId) = await SetupAsync(Vk);
        await using var _ = agent;
        await ApproveAsync(admin, partId);

        await SetModelsAsync(admin, partId, (Ar, null), (Ov, null), (Vk, null));
        Composition(await RerunAsync(partId)).ShouldBe([$"{Ar}=АР", $"{Ov}=ОВ"]);

        // Конфигурация та же, но состав другой — утвердить можно, и следующий прогон берёт новый состав.
        (await ApproveAsync(admin, partId)).ShouldBe(2);
        Composition(await RerunAsync(partId)).ShouldBe([$"{Ar}=АР", $"{Ov}=ОВ", $"{Vk}=ВК"]);
        (await admin.GetJsonAsync<ClashPartView>($"/api/clash/parts/{partId}")).CompositionPending.ShouldBeFalse();
    }

    [Test]
    public async Task Approve_SameConfigurationAndComposition_NothingToApprove()
    {
        var (admin, agent, partId) = await SetupAsync();
        await using var _ = agent;
        await ApproveAsync(admin, partId);

        var response = await admin.PostJsonAsync($"/api/clash/parts/{partId}/approve", new { comment = (string?)null, run = false });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task Diff_ModelAddedRemovedDiscipline()
    {
        var (admin, agent, partId) = await SetupAsync(Vk);
        await using var _ = agent;
        await ApproveAsync(admin, partId);

        await SetModelsAsync(admin, partId, (Ar, "КР"), (Vk, null));

        var preview = await admin.GetJsonAsync<ApprovePreview>($"/api/clash/parts/{partId}/approve/preview");
        var models = preview.Changes.Where(c => c.Kind is ConfigChangeKind.ModelAdded or ConfigChangeKind.ModelRemoved or ConfigChangeKind.ModelDiscipline)
            .Select(c => c.Text).ToArray();
        models.ShouldBe([$"Модель «{Vk}» добавлена", $"Модель «{Ov}» убрана", $"Раздел модели «{Ar}»: АР → КР"], ignoreOrder: true);
    }

    [Test]
    public async Task VersionChanges_IncludeComposition()
    {
        var (admin, agent, partId) = await SetupAsync(Vk);
        await using var _ = agent;
        await ApproveAsync(admin, partId);
        await SetModelsAsync(admin, partId, (Ar, null), (Ov, null), (Vk, null));
        await ApproveAsync(admin, partId);

        var changes = await admin.GetJsonAsync<ConfigChange[]>($"/api/clash/parts/{partId}/versions/2/changes");

        changes.Select(c => c.Text).ShouldContain($"Модель «{Vk}» добавлена");
    }

    [Test]
    public async Task Version_WithoutSnapshot_FallsBackToCurrentModels()
    {
        var (admin, agent, partId) = await SetupAsync(Vk);
        await using var _ = agent;
        var version = await ApproveAsync(admin, partId);
        // Версия стенда, утверждённая до снимка состава.
        await DbAsync(db => db.Set<ClashPartVersion>().Where(v => v.PartId == partId && v.Version == version)
            .ExecuteUpdateAsync(v => v.SetProperty(x => x.ModelsJson, (string?)null)));

        await SetModelsAsync(admin, partId, (Ar, null), (Ov, null), (Vk, null));

        Composition(await RerunAsync(partId)).ShouldBe([$"{Ar}=АР", $"{Ov}=ОВ", $"{Vk}=ВК"]);
        (await admin.GetJsonAsync<ClashPartView>($"/api/clash/parts/{partId}")).CompositionPending.ShouldBeFalse();
    }
}
