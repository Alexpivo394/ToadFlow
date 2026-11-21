using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Serialization;
using ToadFlow.Contracts;
using ToadFlow.Contracts.Families;

namespace ToadFlow.Revit.Addin.Logic;

/// <summary>Чтение семейства без Revit: сверка хеша, размер картинки, формат задания и итога.</summary>
public static class FamilyReadLogic
{
    public const string ChangedNote = "файл изменился после обхода";

    /// <summary>Сторона картинки и запасная, если картинка вышла больше <see cref="MaxPreviewBytes" />.</summary>
    public const int PreviewPixels = 256;

    public const int SmallPreviewPixels = 128;
    public const int MaxPreviewBytes = 150 * 1024;

    /// <summary>
    ///     Как <see cref="AddinPipeJson" />, но ключи словарей как есть: в них имена параметров и столбцов каталога («Width» не должно
    ///     стать «width»).
    /// </summary>
    private static readonly JsonSerializerSettings Settings = new()
    {
        ContractResolver = new DefaultContractResolver { NamingStrategy = new CamelCaseNamingStrategy { ProcessDictionaryKeys = false } },
        Converters = { new StringEnumConverter() },
        Formatting = Formatting.None
    };

    public static FamilyReadPayload ParsePayload(string json) =>
        JsonConvert.DeserializeObject<FamilyReadPayload>(json, Settings) ?? throw new InvalidOperationException("Пустое задание чтения семейства");

    public static string SerializeResult(FamilyReadResult result) => JsonConvert.SerializeObject(result, Settings);

    /// <summary>Файл на диске уже не тот, что видел обход: читать его под этой версией нельзя, следующий обход заведёт новую.</summary>
    public static (JobOutcome Outcome, string Note)? CheckHash(string expected, string actual) =>
        string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase) ? null : (JobOutcome.Warning, ChangedNote);

    /// <summary>
    ///     Картинка не больше <see cref="MaxPreviewBytes" />: сначала со стороной <see cref="PreviewPixels" />, слишком большая — ещё раз
    ///     со стороной <see cref="SmallPreviewPixels" />, и снова большая — без картинки. Ошибка съёмки — тоже без картинки: содержимое важнее.
    /// </summary>
    public static byte[]? Preview(Func<int, byte[]?> render)
    {
        foreach (var pixels in new[] { PreviewPixels, SmallPreviewPixels })
        {
            byte[]? image;
            try
            {
                image = render(pixels);
            }
            catch (Exception)
            {
                return null;
            }

            if (image is null) return null;
            if (image.Length <= MaxPreviewBytes) return image;
        }

        return null;
    }

    /// <summary>Revit не открыл или не прочитал файл: файл виноват, повтор не поможет.</summary>
    /// <remarks>
    ///     Файла уже нет на месте — его переложили после обхода: как изменение, следующий обход разберётся. Сетевая папка не
    ///     ответила — сбой инфраструктуры, задание повторится.
    /// </remarks>
    public static (JobOutcome Outcome, string Note) MapFailure(Exception exception) => exception switch
    {
        OperationCanceledException => (JobOutcome.Cancelled, "отменено пользователем"),
        FileNotFoundException or DirectoryNotFoundException => (JobOutcome.Warning, ChangedNote),
        IOException or UnauthorizedAccessException => (JobOutcome.InfrastructureFailure, $"файл недоступен: {exception.Message}"),
        _ => (JobOutcome.Failed, $"Revit не открыл файл: {exception.Message}")
    };
}
