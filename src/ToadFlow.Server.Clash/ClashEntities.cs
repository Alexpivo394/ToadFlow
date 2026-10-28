using Alexpivo394.Revit.Clash.Rules;

namespace ToadFlow.Server.Clash;

/// <summary>
///     Модель, которую выгружают для коллизий. Опознаётся по пути (<see cref="Key" />); элемент коллизии —
///     (<see cref="Id" />, UniqueId). Перенесённая на другой путь модель становится новой.
/// </summary>
public sealed class ClashModel
{
    public Guid Id { get; set; }

    /// <summary>Нормализованный путь в нижнем регистре, как ключ модели в аналитике LOI.</summary>
    public string Key { get; set; } = "";

    public string Name { get; set; } = "";
    public string ModelPath { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Ключ модели в файле слепка (<c>ModelHeader.ModelKey</c>).</summary>
    public string SnapshotKey => Id.ToString("N");
}

/// <summary>Опубликованный слепок модели: <c>model.tcg</c> и <c>model.tcp</c> в хранилище, проверенные целиком.</summary>
public sealed class ClashSnapshot
{
    public Guid Id { get; set; }
    public Guid ModelId { get; set; }
    public Guid JobId { get; set; }
    public DateTimeOffset ExtractedAt { get; set; }
    public int RevitYear { get; set; }
    public string ViewName { get; set; } = "";
    public int Elements { get; set; }
    public long Triangles { get; set; }
    public int Inventory { get; set; }
    public long GeometryBytes { get; set; }
    public long ParametersBytes { get; set; }
    public ModelCatalog Catalog { get; set; } = ModelCatalog.Merge([]);
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Загрузка слепка агентом: идёт, пока не опубликована (<see cref="SnapshotId" />) или не брошена.</summary>
public sealed class ClashUpload
{
    public Guid Id { get; set; }
    public Guid JobId { get; set; }
    public Guid AgentId { get; set; }
    public Guid ModelId { get; set; }
    public long GeometryBytes { get; set; }
    public string GeometrySha256 { get; set; } = "";
    public long ParametersBytes { get; set; }
    public string ParametersSha256 { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? SnapshotId { get; set; }
}
