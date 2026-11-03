using Microsoft.Extensions.Options;

namespace ToadFlow.Server.Clash.Engine;

/// <summary>Настройки движка коллизий на стороне сервера — секция <c>Clash</c> (в Docker: <c>Clash__EngineKey</c> из <c>CLASH_ENGINE_KEY</c>).</summary>
public sealed class ClashEngineOptions
{
    public const string Section = "Clash";

    /// <summary>Наименьшая длина ключа движка: короткий ключ подбирается.</summary>
    public const int MinKeyLength = 32;

    /// <summary>Ключ работника (<c>Clash:EngineKey</c>). Пустой — движка в этой установке нет.</summary>
    public string EngineKey { get; set; } = "";
}

/// <summary>Непустой ключ движка короче <see cref="ClashEngineOptions.MinKeyLength" /> символов — сервер не стартует.</summary>
public sealed class ClashEngineOptionsValidator : IValidateOptions<ClashEngineOptions>
{
    public ValidateOptionsResult Validate(string? name, ClashEngineOptions options) =>
        options.EngineKey is { Length: > 0 and < ClashEngineOptions.MinKeyLength }
            ? ValidateOptionsResult.Fail($"CLASH_ENGINE_KEY (Clash:EngineKey) короче {ClashEngineOptions.MinKeyLength} символов: сгенерируйте ключ командой openssl rand -base64 48")
            : ValidateOptionsResult.Success;
}
