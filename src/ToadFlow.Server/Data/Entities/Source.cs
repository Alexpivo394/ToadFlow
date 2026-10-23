using ToadFlow.Contracts;

namespace ToadFlow.Server.Data.Entities;

public sealed class Source
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public SourceKind Kind { get; set; }

    /// <summary>Имя Revit Server или корневая папка (UNC или диск).</summary>
    public string Host { get; set; } = "";

    /// <summary>Версия Revit Server; у папки null.</summary>
    public int? RevitYear { get; set; }

    public string RootPath { get; set; } = "";

    public SourceDto ToDto() => new(Id, Kind, Host, RevitYear, RootPath);
}
