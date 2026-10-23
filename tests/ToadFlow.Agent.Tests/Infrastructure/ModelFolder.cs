using System.Text;
using OpenMcdf;

namespace ToadFlow.Agent.Tests.Infrastructure;

/// <summary>Временная папка с «моделями»: составные .rvt с потоком BasicFileInfo, как у Revit.</summary>
public sealed class ModelFolder : IDisposable
{
    public string Root { get; } = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "toadflow-src-" + Guid.NewGuid().ToString("N"))).FullName;

    public string Model(string relative, int? year)
    {
        var path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var root = RootStorage.Create(path);
        if (year is { } y)
        {
            using var stream = root.CreateStream("BasicFileInfo");
            var data = Encoding.Unicode.GetBytes($"Worksharing: Not enabled\r\nFormat: {y}\r\n");
            stream.Write(data, 0, data.Length);
        }

        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, true);
        }
        catch (IOException)
        {
            // файл ещё открыт — временная папка, не страшно
        }
    }
}
