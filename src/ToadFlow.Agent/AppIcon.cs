namespace ToadFlow.Agent.App;

/// <summary>Логотип ToadFlow из ресурсов сборки — кадр нужного размера, без размытия при масштабировании.</summary>
internal static class AppIcon
{
    public static Icon Get(Size size)
    {
        using var stream = typeof(AppIcon).Assembly.GetManifestResourceStream("toadflow.ico")!;
        return new Icon(stream, size);
    }
}
