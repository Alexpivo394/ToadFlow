using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace ToadFlow.Server.Auth;

/// <summary>Частота анонимных входа и регистрации с одного адреса (<c>Auth:Limits</c>).</summary>
public sealed class AuthLimits
{
    public const string Section = "Auth:Limits";

    /// <summary>Попыток входа в минуту с одного адреса — подбор пароля.</summary>
    public int LoginPerMinute { get; set; } = 10;

    /// <summary>Заявок на регистрацию за 10 минут с одного адреса.</summary>
    public int RegisterPer10Minutes { get; set; } = 5;
}

/// <summary>
///     Лимиты по IP. За nginx адрес клиента — из <c>X-Forwarded-For</c>: заголовок принимается только от прокси из частных сетей (сеть
///     compose), иначе любой мог бы подставить чужой адрес и обойти лимит.
/// </summary>
public static class AuthRateLimits
{
    public const string Login = "auth-login";
    public const string Register = "auth-register";

    public static void AddAuthRateLimits(this WebApplicationBuilder builder)
    {
        builder.Services.AddOptions<AuthLimits>().BindConfiguration(AuthLimits.Section);
        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            // Прокси может быть два (HTTPS-прокси на хосте → nginx в compose): адреса из частных сетей пропускаются,
            // пока не встретится внешний адрес клиента. С пределом 1 клиентом был бы второй прокси — общий на всех.
            options.ForwardLimit = null;
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();
            foreach (var network in new[] { "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "127.0.0.0/8", "::1/128" })
                options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
        });
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = async (context, ct) =>
                await context.HttpContext.Response.WriteAsJsonAsync(new { error = "Слишком много попыток — подождите немного и попробуйте снова" }, ct);
            options.AddPolicy(Login, http => Window(http, l => l.LoginPerMinute, TimeSpan.FromMinutes(1)));
            options.AddPolicy(Register, http => Window(http, l => l.RegisterPer10Minutes, TimeSpan.FromMinutes(10)));
        });
    }

    private static RateLimitPartition<string> Window(HttpContext http, Func<AuthLimits, int> limit, TimeSpan window)
    {
        var permits = limit(http.RequestServices.GetRequiredService<IOptions<AuthLimits>>().Value);
        var address = http.Connection.RemoteIpAddress ?? IPAddress.None;
        return RateLimitPartition.GetFixedWindowLimiter($"{address}", _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permits,
            Window = window,
            QueueLimit = 0
        });
    }
}
