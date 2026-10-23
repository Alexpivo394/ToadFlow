using System;

namespace ToadFlow.Contracts;

/// <param name="Host">Имя Revit Server или корневая папка (UNC или диск).</param>
/// <param name="RevitYear">Версия Revit Server; у папки null — версия своя у каждого файла.</param>
public sealed record SourceDto(Guid Id, SourceKind Kind, string Host, int? RevitYear, string RootPath);

public sealed record SourceEntryDto(string Name, string FolderPath, string ModelPath, bool IsFolder);

/// <summary>
///     Что выгружать из источника: явно отмеченные модели и папки, из которых модели берутся автоматически (рекурсивно).
///     Явная модель переопределяет форматы своей папки.
/// </summary>
public sealed record SelectionDto(ModelSelectionDto[] Models, FolderSelectionDto[] Folders);

public sealed record ModelSelectionDto(string Name, string ModelPath, bool Rvt, bool Nwc);

public sealed record FolderSelectionDto(string FolderPath, bool Rvt, bool Nwc);

public sealed record ExpandedModelDto(string Name, string ModelPath, bool IsServer, int? RevitYear, bool Rvt, bool Nwc);
