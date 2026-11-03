using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using ToadFlow.Contracts.Clash;

namespace ToadFlow.Server.Clash.Engine;

/// <summary>
///     Работник движка входит ключом <c>Clash:EngineKey</c> из конфигурации сервера (заголовок <see cref="EngineApi.KeyHeader" />).
///     Ключ не задан — работник не войдёт: движка в этой установке нет.
/// </summary>
public sealed class EngineAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, IOptions<ClashEngineOptions> engineOptions)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Engine";
    public const string Policy = "Engine";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var key = Request.Headers[EngineApi.KeyHeader].FirstOrDefault();
        if (string.IsNullOrEmpty(key)) return Task.FromResult(AuthenticateResult.NoResult());

        var expected = engineOptions.Value.EngineKey;
        if (string.IsNullOrEmpty(expected) || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(expected)))
            return Task.FromResult(AuthenticateResult.Fail("Неверный ключ движка"));

        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "clash-engine")], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}
