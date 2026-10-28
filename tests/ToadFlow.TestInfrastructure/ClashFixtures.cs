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
    public ClashUploadRequest Request(Guid jobId) => new(jobId, Geometry.Length, Sha(Geometry), Parameters.Length, Sha(Parameters));

    public static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}

public static class ClashFixtures
{
    /// <param name="padding">Сколько добавить элементов-коробок: так файл геометрии становится больше.</param>
    public static ClashFiles ModelFiles(string modelKey, bool complete = true, DateTimeOffset? extractedAt = null, int padding = 0)
    {
        var header = new ModelHeader(modelKey, @"C:\models\АР.rvt", 2022, extractedAt ?? DateTimeOffset.UtcNow, complete, "Navisworks", 0, 0, 0);
        var elements = new List<ElementGeometry>
        {
            Box(new ElementInfo("wall-1", 1, "OST_Walls", "Стена", "200", "Этаж 1", "АР", []), 0),
            Box(new ElementInfo("pipe-1", 2, "OST_PipeCurves", "Труба", "Сталь", "Этаж 1", "ВК", []), 1000)
        };
        for (var i = 0; i < padding; i++) elements.Add(Box(new ElementInfo($"pad-{i}", 100 + i, "OST_GenericModel", null, null, null, null, []), 2000 + i * 10));
        var geometry = new ModelGeometry(header, elements, elements.Select(e => e.Info.UniqueId).Append("hidden-1").ToArray());
        var parameters = new ModelParameters(new Dictionary<string, IReadOnlyList<ParameterValue>>
        {
            ["wall-1"] = [new ParameterValue("Марка", null, "С1", null)],
            ["pipe-1"] = [new ParameterValue("Диаметр", null, "50 мм", 50)]
        });

        using var tcg = new MemoryStream();
        ModelGeometryFile.Write(tcg, geometry);
        using var tcp = new MemoryStream();
        ModelParametersFile.Write(tcp, parameters);
        return new ClashFiles(tcg.ToArray(), tcp.ToArray(), elements.Count, elements.Sum(e => (long)e.Mesh.TriangleCount), geometry.Inventory.Count);
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

    private static ElementGeometry Box(ElementInfo info, float x)
    {
        float[] positions = [x, 0, 0, x + 100, 0, 0, x + 100, 100, 0, x, 100, 0, x, 0, 100, x + 100, 0, 100, x + 100, 100, 100, x, 100, 100];
        int[] indices = [0, 2, 1, 0, 3, 2, 4, 5, 6, 4, 6, 7, 0, 1, 5, 0, 5, 4, 1, 2, 6, 1, 6, 5, 2, 3, 7, 2, 7, 6, 3, 0, 4, 3, 4, 7];
        var mesh = new ElementMesh(positions, indices);
        return new ElementGeometry(info, mesh, new Aabb(new Vector3(x, 0, 0), new Vector3(x + 100, 100, 100)), (ulong)(x + 1));
    }
}
