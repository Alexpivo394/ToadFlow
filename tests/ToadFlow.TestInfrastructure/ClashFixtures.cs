using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using Alexpivo394.Revit.Clash.Geometry;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ToadFlow.Contracts;
using ToadFlow.Contracts.Clash;
using ToadFlow.Server.Clash;
using ToadFlow.Server.Data;

namespace ToadFlow.Server.Tests.Infrastructure;

/// <summary>Слепок модели в памяти: те же файлы, что пишет аддин.</summary>
public sealed record ClashFiles(byte[] Geometry, byte[] Parameters, int Elements, long Triangles, int Inventory)
{
    public ClashUploadRequest Request(Guid jobId, int? attempt = null) =>
        new(jobId, Geometry.Length, Sha(Geometry), Parameters.Length, Sha(Parameters), attempt);

    public static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}

public static class ClashFixtures
{
    /// <param name="padding">Сколько добавить элементов-коробок: так файл геометрии становится больше.</param>
    /// <param name="custom">Своя геометрия по ключу модели вместо стены и трубы; параметры те же.</param>
    public static ClashFiles ModelFiles(string modelKey, bool complete = true, DateTimeOffset? extractedAt = null, int padding = 0,
        Func<string, ModelGeometry>? custom = null)
    {
        var header = new ModelHeader(modelKey, @"C:\models\АР.rvt", 2022, extractedAt ?? DateTimeOffset.UtcNow, complete, "Navisworks", 0, 0, 0);
        var elements = new List<ElementGeometry>
        {
            Box(new ElementInfo("wall-1", 1, "OST_Walls", "Стена", "200", "Этаж 1", "АР", []), 0),
            Box(new ElementInfo("pipe-1", 2, "OST_PipeCurves", "Труба", "Сталь", "Этаж 1", "ВК", []), 1000)
        };
        for (var i = 0; i < padding; i++) elements.Add(Box(new ElementInfo($"pad-{i}", 100 + i, "OST_GenericModel", null, null, null, null, []), 2000 + i * 10));
        var geometry = custom?.Invoke(modelKey) ?? new ModelGeometry(header, elements, elements.Select(e => e.Info.UniqueId).Append("hidden-1").ToArray());
        var parameters = new ModelParameters(new Dictionary<string, IReadOnlyList<ParameterValue>>
        {
            ["wall-1"] = [new ParameterValue("Марка", null, "С1", null)],
            ["pipe-1"] = [new ParameterValue("Диаметр", null, "50 мм", 50)]
        });

        using var tcg = new MemoryStream();
        ModelGeometryFile.Write(tcg, geometry);
        using var tcp = new MemoryStream();
        ModelParametersFile.Write(tcp, parameters);
        return new ClashFiles(tcg.ToArray(), tcp.ToArray(), geometry.Elements.Count, geometry.Elements.Sum(e => (long)e.Mesh.TriangleCount),
            geometry.Inventory.Count);
    }

    /// <summary>Задание выгрузки модели, взятое агентом: такие задания принимают загрузку.</summary>
    public static async Task<(Guid JobId, ClashModel Model)> RunningJobAsync(ServerFactory server, FakeAgent agent, string modelName = "АР.rvt")
    {
        using var scope = server.Services.CreateScope();
        var storage = scope.ServiceProvider.GetRequiredService<ClashStorage>();
        var model = await storage.EnsureModelAsync(modelName, $"RSN://rs22/ЖК/{modelName}", CancellationToken.None);
        var db = scope.ServiceProvider.GetRequiredService<ToadFlowDb>();
        var run = Seed.Run(db);
        var job = Seed.Job(db, run.Id);
        job.ModuleKind = ModuleKinds.ClashExtract;
        job.ModelName = modelName;
        job.PayloadJson = JsonSerializer.Serialize(
            new ClashExtractPayload(modelName, model.ModelPath, true, 2022, model.SnapshotKey, "Navisworks", ""), ToadFlowDb.Json);
        await db.SaveChangesAsync();

        var assignment = await agent.RequestJobAsync(1, 2022) ?? throw new InvalidOperationException("агент не взял задание");
        if (assignment.JobId != job.Id) throw new InvalidOperationException("агент взял другое задание");
        return (job.Id, model);
    }

    /// <summary>Клиент с ключом агента — как у настоящего агента.</summary>
    public static HttpClient AgentClient(ServerFactory server, FakeAgent agent)
    {
        var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Agent-Id", agent.Id.ToString());
        client.DefaultRequestHeaders.Add("X-Agent-Key", agent.Key);
        return client;
    }

    /// <summary>Выгрузка модели целиком, как её делает агент: задание, загрузка обоих файлов одной частью, публикация.</summary>
    public static async Task<(Guid JobId, ClashModel Model, ClashExtractResult Result)> PublishAsync(ServerFactory server, FakeAgent agent,
        string modelName = "АР.rvt", DateTimeOffset? extractedAt = null, int padding = 0, Func<string, ModelGeometry>? custom = null)
    {
        var (jobId, model) = await RunningJobAsync(server, agent, modelName);
        var files = ModelFiles(model.SnapshotKey, extractedAt: extractedAt, padding: padding, custom: custom);
        var client = AgentClient(server, agent);
        var started = await client.PostJsonAsync("/api/agent/clash/uploads", files.Request(jobId));
        var upload = await started.ReadJsonAsync<ClashUploadState>();
        foreach (var (file, bytes) in new[] { (ClashUploads.Geometry, files.Geometry), (ClashUploads.Parameters, files.Parameters) })
        {
            var put = await client.PutAsync($"/api/agent/clash/uploads/{upload.UploadId}/{file}?offset=0", new ByteArrayContent(bytes));
            put.EnsureSuccessStatusCode();
        }

        var completed = await client.PostAsync($"/api/agent/clash/uploads/{upload.UploadId}/complete", null);
        completed.EnsureSuccessStatusCode();
        return (jobId, model, await completed.ReadJsonAsync<ClashExtractResult>());
    }

    private static ElementGeometry Box(ElementInfo info, float x) => Box(info, x, 0, 0, 100);

    /// <summary>Замкнутый куб: угол <paramref name="x" />, <paramref name="y" />, <paramref name="z" /> (мм от начала модели), ребро <paramref name="size" />.</summary>
    public static ElementGeometry Box(ElementInfo info, float x, float y, float z, float size)
    {
        float x1 = x + size, y1 = y + size, z1 = z + size;
        float[] positions = [x, y, z, x1, y, z, x1, y1, z, x, y1, z, x, y, z1, x1, y, z1, x1, y1, z1, x, y1, z1];
        int[] indices = [0, 2, 1, 0, 3, 2, 4, 5, 6, 4, 6, 7, 0, 1, 5, 0, 5, 4, 1, 2, 6, 1, 6, 5, 2, 3, 7, 2, 7, 6, 3, 0, 4, 3, 4, 7];
        var mesh = new ElementMesh(positions, indices);
        return new ElementGeometry(info, mesh, new Aabb(new Vector3(x, y, z), new Vector3(x1, y1, z1)), (ulong)(x + 1));
    }
}
