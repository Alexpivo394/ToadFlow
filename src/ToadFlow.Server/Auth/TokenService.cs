using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using ToadFlow.Server.Data.Entities;

namespace ToadFlow.Server.Auth;

public sealed class TokenService(IOptions<AuthOptions> options, TimeProvider time)
{
    public const string Issuer = "toadflow";

    public static SymmetricSecurityKey Key(string jwtKey) => new(Encoding.UTF8.GetBytes(jwtKey));

    public string Issue(User user)
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.DisplayName),
            new Claim(ClaimTypes.Role, user.Role.ToString())
        };
        var now = time.GetUtcNow().UtcDateTime;
        var token = new JwtSecurityToken(Issuer, Issuer, claims, now, now.AddHours(options.Value.TokenLifetimeHours),
            new SigningCredentials(Key(options.Value.JwtKey), SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
