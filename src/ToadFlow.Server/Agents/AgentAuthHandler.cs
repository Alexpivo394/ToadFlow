using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ToadFlow.Contracts;
using ToadFlow.Server.Data;

namespace ToadFlow.Server.Agents;

/// <summary>
///     Агент авторизуется своим ключом: заголовки <c>X-Agent-Id</c> и <c>X-Agent-Key</c> (или параметры <c>agentId</c>,
///     <c>agentKey</c> в строке запроса — для WebSocket). Отключённый агент не проходит.
/// </summary>
public sealed class AgentAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, ToadFlowDb db)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Agent";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var idText = Header("X-Agent-Id", "agentId");
        var key = Header("X-Agent-Key", "agentKey");
        if (idText is null || key is null) return AuthenticateResult.NoResult();
        if (!Guid.TryParse(idText, out var agentId)) return AuthenticateResult.Fail("Неверный идентификатор агента");

        var agent = await db.Agents.AsNoTracking().SingleOrDefaultAsync(a => a.Id == agentId);
        if (agent is null || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(agent.KeyHash), Encoding.ASCII.GetBytes(Hash(key))))
            return AuthenticateResult.Fail("Неверный ключ агента");
        if (agent.State == AgentState.Disabled) return AuthenticateResult.Fail("Агент отключён");

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, agent.Id.ToString()), new Claim(ClaimTypes.Name, agent.MachineName)], SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    public static string Hash(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();

    private string? Header(string header, string query)
    {
        var value = Request.Headers[header].FirstOrDefault();
        if (string.IsNullOrEmpty(value)) value = Request.Query[query].FirstOrDefault();
        return string.IsNullOrEmpty(value) ? null : value;
    }
}
