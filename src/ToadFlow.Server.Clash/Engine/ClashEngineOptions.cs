using Microsoft.Extensions.Options;

namespace ToadFlow.Server.Clash.Engine;

/// <summary>Настройки движка коллизий на стороне сервера — секция <c>Clash</c> (в Docker: <c>Clash__EngineKey</c> из <c>CLASH_ENGINE_KEY</c>).</summary>
public sealed class ClashEngineOptions
{
    public const string Section = "Clash";

    /// <summary>Наименьшая длина ключа движка: короткий ключ подбирается.</summary>
    public const int MinKeyLength = 32;

    /// <summary>Ключ работника (<c>Clash:EngineKey</c>). Пустой — движка в этой установке нет (допустимо только вне рабочей установки).</summary>
    public string EngineKey { get; set; } = "";
}

/// <summary>
///     Непустой ключ движка короче <see cref="ClashEngineOptions.MinKeyLength" /> символов — сервер не стартует. На рабочей установке
///     (окружение Production) не стартует и с пустым ключом: compose ключ не требует, потому что коллизии можно выключить, а
///     включённым коллизиям без ключа движок не подключится. Проверка есть, только пока модуль включён: его службы регистрируются
///     лишь тогда.
/// </summary>
public sealed class ClashEngineOptionsValidator(IHostEnvironment environment) : IValidateOptions<ClashEngineOptions>
{
    public ValidateOptionsResult Validate(string? name, ClashEngineOptions options) => options.EngineKey switch
    {
        { Length: > 0 and < ClashEngineOptions.MinKeyLength } => ValidateOptionsResult.Fail(
            $"CLASH_ENGINE_KEY (Clash:EngineKey) короче {ClashEngineOptions.MinKeyLength} символов: сгенерируйте ключ командой openssl rand -base64 48"),
        { Length: 0 } when environment.IsProduction() => ValidateOptionsResult.Fail(
            "CLASH_ENGINE_KEY (Clash:EngineKey) не задан, а модуль коллизий включён: задайте ключ в .env (openssl rand -base64 48) или уберите clash из MODULES"),
        _ => ValidateOptionsResult.Success
    };
}
