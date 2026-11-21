using System.IO;
using Alexpivo394.Revit.Families.Files;
using Alexpivo394.Revit.Families.Revit;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Nice3point.Revit.Toolkit;
using ToadFlow.Contracts;
using ToadFlow.Contracts.Families;
using ToadFlow.Contracts.Pipe;
using ToadFlow.Revit.Addin.Logic;

namespace ToadFlow.Revit.Addin;

/// <summary>
///     Чтение семейства из библиотеки в потоке Revit API: сверить хеш с тем, что видел обход, открыть файл, прочитать содержимое
///     (<see cref="FamilyReader" />) и снять картинку (<see cref="FamilyPreview" />), закрыть без сохранения. Файл не меняется.
/// </summary>
internal sealed class FamilyReadRunner(PipeProgress progress)
{
    public (JobOutcome Outcome, string? Note, string? ResultJson) Run(UIApplication application, JobMessage job)
    {
        var payload = FamilyReadLogic.ParsePayload(job.PayloadJson);
        var name = Path.GetFileName(payload.Path);
        try
        {
            progress.Status("Сверка файла");
            if (FamilyReadLogic.CheckHash(payload.Hash, FamilyHash.OfFile(payload.Path)) is { } changed)
            {
                progress.Step(name, changed.Note);
                return (changed.Outcome, changed.Note, null);
            }

            TypeCatalog? catalog = null;
            if (payload.CatalogPath is { } catalogPath && File.Exists(catalogPath))
            {
                try
                {
                    catalog = TypeCatalogParser.ParseFile(catalogPath);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException)
                {
                    progress.Step(name, $"каталог типоразмеров не прочитан: {exception.Message}");
                }
            }

            progress.Status("Открытие семейства");
            Document document;
            // Предупреждения и окна при открытии некому закрыть: ферма работает без человека.
            using (RevitApiContext.BeginFailureSuppressionScope())
            using (RevitContext.BeginDialogSuppressionScope())
            {
                document = application.Application.OpenDocumentFile(payload.Path);
            }

            try
            {
                if (!document.IsFamilyDocument) return (JobOutcome.Failed, "файл не является семейством Revit", null);

                progress.Status("Чтение семейства");
                var content = FamilyReader.Read(document, catalog);
                var preview = FamilyReadLogic.Preview(pixels => FamilyPreview.Render(document, pixels));
                progress.Step(name, $"категория «{content.Category}», типоразмеров {content.Types.Count}, параметров {content.Parameters.Count}" +
                                    (preview is null ? ", без картинки" : ""));
                var result = new FamilyReadResult(content, preview is null ? null : Convert.ToBase64String(preview));
                return (JobOutcome.Succeeded, null, FamilyReadLogic.SerializeResult(result));
            }
            finally
            {
                document.Close(false);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var (outcome, note) = FamilyReadLogic.MapFailure(exception);
            progress.Step(name, note);
            return (outcome, note, null);
        }
    }
}
