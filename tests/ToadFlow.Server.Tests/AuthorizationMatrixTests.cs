using System.Net;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Server.Auth;
using ToadFlow.Server.Tests.Infrastructure;

namespace ToadFlow.Server.Tests;

/// <summary>Наблюдатель ничего не меняет: все изменяющие маршруты API ему запрещены. Новые маршруты попадают сюда сами.</summary>
public class AuthorizationMatrixTests : ServerTest
{
    private static readonly string[] Open = ["/api/auth/login", "/api/auth/register", "/api/agents/register"];

    /// <summary>
    ///     Доступно и наблюдателю: свой профиль, своя отметка захода в «Мои коллизии», свои токены доступа, своя ручная проверка из ParamChecker и предпросмотры разбора имён и
    ///     профилей моделей — они ничего не сохраняют, POST только из-за тела запроса. Свой архив выгрузки отчёта: выгрузка
    ///     открыта любой роли.
    /// </summary>
    private static readonly string[] Personal =
        ["/api/auth/tokens", "/api/auth/tokens/current", "/api/auth/tokens/{id:guid}", "/api/profile", "/api/profile/password", "/api/loi/checks", "/api/loi/naming/preview", "/api/loi/routing/preview",
         "/api/clash/parts/{id:guid}/preview", "/api/clash/parts/{id:guid}/values", "/api/clash/parts/{id:guid}/unassigned",
         "/api/clash/parts/{id:guid}/presence", "/api/clash/my/visit", "/api/clash/reports/{runId:guid}/archive", "/api/clash/report-archives/{id:guid}"];

    [Test]
    public async Task Authorization_MutatingEndpoints_RejectViewer()
    {
        var viewer = await ClientAsync(UserRole.Viewer);
        var routes = Server.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .SelectMany(endpoint => (endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [])
                .Select(method => (method, path: "/" + endpoint.RoutePattern.RawText!.Trim('/'))))
            .Where(route => route.method is "POST" or "PUT" or "DELETE" && route.path.StartsWith("/api/") && !Open.Contains(route.path)
                            && !Personal.Contains(route.path))
            .Distinct()
            .ToList();

        routes.ShouldNotBeEmpty();
        var allowed = new List<string>();
        foreach (var (method, path) in routes)
        {
            var url = System.Text.RegularExpressions.Regex.Replace(path, @"\{[^}]+\}", match =>
                match.Value.Contains("slot") ? "1" : Guid.NewGuid().ToString());
            using var request = new HttpRequestMessage(new HttpMethod(method), url)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };

            var response = await viewer.SendAsync(request);
            // Маршруты агентов и движка пускают только по своему ключу: пользователя они не узнают вовсе (401), а не запрещают (403).
            var rejected = response.StatusCode == HttpStatusCode.Forbidden
                           || ((path.StartsWith("/api/agent/") || path.StartsWith("/api/engine/")) && response.StatusCode == HttpStatusCode.Unauthorized);
            if (!rejected) allowed.Add($"{method} {path} → {(int)response.StatusCode}");
        }

        allowed.ShouldBeEmpty();
    }

    /// <summary>Политики, которых нет у проектировщика: маршрут с любой из них ему закрыт.</summary>
    private static readonly string[] Staff = [Policies.Viewer, Policies.Operator, Policies.Admin];

    /// <summary>
    ///     Проектировщик видит только маршруты участников (<c>MapMemberEndpoints</c>, вход, свои токены, health): у них нет
    ///     политик наблюдателя и старше. Остальное ему 403, агентам и движку — 401. Новые маршруты попадают сюда сами.
    /// </summary>
    [Test]
    public async Task Authorization_DesignerSeesOnlyMemberRoutes()
    {
        var designer = await ClientAsync(UserRole.Designer);
        var routes = Server.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .SelectMany(endpoint => (endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [])
                .Select(method => (method, path: "/" + endpoint.RoutePattern.RawText!.Trim('/'),
                    staff: endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Any(data => Staff.Contains(data.Policy)))))
            .Where(route => route.path.StartsWith("/api/") && !Open.Contains(route.path))
            .Distinct()
            .ToList();

        var members = routes.Where(route => !route.staff && !route.path.StartsWith("/api/agent/") && !route.path.StartsWith("/api/engine/"))
            .Select(route => $"{route.method} {route.path}").ToList();
        members.ShouldContain("GET /api/clash/collisions/{id:guid}");
        members.ShouldContain("GET /api/clash/collisions/{id:guid}/image");
        members.ShouldContain("GET /api/clash/parts/{id:guid}/approved/collisions");
        members.ShouldContain("GET /api/clash/parts/{id:guid}/versions");
        members.ShouldContain("POST /api/clash/collisions/{id:guid}/status");
        members.ShouldContain("POST /api/clash/collisions/{id:guid}/assignee");
        members.ShouldContain("POST /api/clash/collisions/{id:guid}/notes");
        members.ShouldNotContain("POST /api/clash/collisions/{id:guid}/merge");
        members.ShouldNotContain("GET /api/clash/parts/{id:guid}");
        members.ShouldContain("POST /api/loi/checks"); // итог ParamChecker — и от проектировщика
        members.Where(route => !route.Contains(" /api/auth/") && !route.EndsWith(" /api/health") && !route.Contains(" /api/clash/")
                               && !route.Contains(" /api/profile") && !route.EndsWith("/card") && route != "POST /api/loi/checks")
            .ShouldBeEmpty();

        var wrong = new List<string>();
        foreach (var (method, path, staff) in routes)
        {
            var url = System.Text.RegularExpressions.Regex.Replace(path, @"\{[^}]+\}", match =>
                match.Value.Contains("slot") || match.Value.Contains(":int") ? "1" : Guid.NewGuid().ToString());
            using var request = new HttpRequestMessage(new HttpMethod(method), url);
            if (method is "POST" or "PUT" or "DELETE") request.Content = new StringContent("{}", Encoding.UTF8, "application/json");

            var response = await designer.SendAsync(request);
            var external = path.StartsWith("/api/agent/") || path.StartsWith("/api/engine/");
            var rejected = response.StatusCode == HttpStatusCode.Forbidden || (external && response.StatusCode == HttpStatusCode.Unauthorized);
            if (rejected != (staff || external)) wrong.Add($"{method} {path} → {(int)response.StatusCode}");
        }

        wrong.ShouldBeEmpty();
        // Живые обновления рассылают события всех модулей — проектировщику их видеть не положено.
        (await designer.PostAsync("/hubs/ui/negotiate?negotiateVersion=1", null)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
