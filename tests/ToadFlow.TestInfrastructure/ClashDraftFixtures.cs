using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Alexpivo394.Revit.Clash.Rules;
using ToadFlow.Contracts;
using ToadFlow.Contracts.Clash;
using ToadFlow.Server.Clash;
using ToadFlow.Server.Clash.Parts;

namespace ToadFlow.Server.Tests.Infrastructure;

/// <summary>Часть с выгруженными моделями и рабочим черновиком; клиент работника движка.</summary>
public static class ClashDraftFixtures
{
    /// <summary>
    ///     Выгружает модели (разделы — по именам, например <c>POL_K1_AR.rvt</c> → АР), собирает из них часть и сохраняет черновик:
    ///     на каждый раздел набор «Все» (категория не пуста) и полная матрица 20 мм.
    /// </summary>
    public static Task<Guid> PartAsync(ServerFactory server, HttpClient admin, FakeAgent agent, params string[] models) =>
        PartAsync(server, admin, agent, models, null);

    /// <param name="custom">Геометрия модели по имени и ключу; null — стена и труба из <see cref="ClashFixtures.ModelFiles" />.</param>
    public static async Task<Guid> PartAsync(ServerFactory server, HttpClient admin, FakeAgent agent, IReadOnlyList<string> models,
        Func<string, string, Alexpivo394.Revit.Clash.Geometry.ModelGeometry>? custom)
    {
        foreach (var model in models)
        {
            var published = await ClashFixtures.PublishAsync(server, agent, model, custom: custom is null ? null : key => custom(model, key));
            await agent.CompleteJobAsync(published.JobId, JobOutcome.Succeeded, null, JsonSerializer.Serialize(published.Result, TestJson.Options));
        }

        var known = await admin.GetJsonAsync<ClashModelView[]>("/api/clash/models");
        var part = await (await admin.PostJsonAsync("/api/clash/parts", new { name = "Часть " + Guid.NewGuid().ToString("N")[..4] })).ReadJsonAsync<ClashPartView>();
        var ids = models.Select(name => known.Single(m => m.Name == name).Id).ToArray();
        (await admin.PutJsonAsync($"/api/clash/parts/{part.Id}/models", ids.Select(id => new { modelId = id, disciplineOverride = (string?)null }).ToArray()))
            .EnsureSuccessStatusCode();

        var disciplines = (await admin.GetJsonAsync<DraftView>($"/api/clash/parts/{part.Id}/draft")).Disciplines;
        var any = new Condition(new PropertyRef(PropertyKind.Category), ConditionOperator.IsNotEmpty, null);
        var config = new ClashConfiguration(disciplines.Select(d => new SelectionSet(Guid.NewGuid(), d, "Все", [new ConditionGroup([any])])).ToList(), [], []);
        (await admin.PutAsync($"/api/clash/parts/{part.Id}/draft", new StringContent(ConfigurationJson.Serialize(config), Encoding.UTF8, "application/json")))
            .EnsureSuccessStatusCode();
        (await admin.PostAsync($"/api/clash/parts/{part.Id}/draft/matrix/fill", null)).EnsureSuccessStatusCode();
        return part.Id;
    }

    public static HttpClient EngineClient(ServerFactory server, string key = ServerFactory.EngineKey)
    {
        var client = server.CreateClient();
        client.DefaultRequestHeaders.Add(EngineApi.KeyHeader, key);
        return client;
    }

    /// <summary>Часть результата сжатым JSON, как шлёт работник.</summary>
    public static Task<HttpResponseMessage> PostResultAsync(HttpClient engine, Guid runId, int part, bool last, IReadOnlyList<EngineHit> hits, EngineRunStats? stats = null)
    {
        using var packed = new MemoryStream();
        using (var zip = new GZipStream(packed, CompressionLevel.Fastest, true))
            JsonSerializer.Serialize(zip, new EngineRunResult(runId, hits, stats), TestJson.Options);
        var content = new ByteArrayContent(packed.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        content.Headers.ContentEncoding.Add("gzip");
        return engine.PostAsync($"/api/engine/runs/{runId}/result?part={part}&last={(last ? "true" : "false")}", content);
    }

    public static EngineHit Hit(int i, string modelA = "a", string modelB = "b", string disciplineA = "АР", string disciplineB = "ОВ", float depth = 30) =>
        new(modelA, $"ua-{i}", i, disciplineA, [], "OST_Walls", "Стена", "200", "Этаж 1",
            modelB, $"ub-{i}", 100_000 + i, disciplineB, [], "OST_DuctCurves", "Воздуховод", "500x300", "Этаж 1", i, 0, 0, depth);

    public static readonly EngineRunStats Stats = new(10, 100, 5, 0.1, 0.1, 0.2, 1 << 20, 1 << 22);
}
