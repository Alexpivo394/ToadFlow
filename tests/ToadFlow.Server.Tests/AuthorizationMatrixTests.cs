using System.Net;
using System.Text;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Server.Tests.Infrastructure;

namespace ToadFlow.Server.Tests;

/// <summary>Наблюдатель ничего не меняет: все изменяющие маршруты API ему запрещены. Новые маршруты попадают сюда сами.</summary>
public class AuthorizationMatrixTests : ServerTest
{
    private static readonly string[] Open = ["/api/auth/login", "/api/agents/register"];

    /// <summary>
    ///     Доступно и наблюдателю: свои токены доступа, своя ручная проверка из ParamChecker и предпросмотры разбора имён и
    ///     профилей моделей — они ничего не сохраняют, POST только из-за тела запроса.
    /// </summary>
    private static readonly string[] Personal =
        ["/api/auth/tokens", "/api/auth/tokens/{id:guid}", "/api/loi/checks", "/api/loi/naming/preview", "/api/loi/routing/preview"];

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
            if (response.StatusCode != HttpStatusCode.Forbidden) allowed.Add($"{method} {path} → {(int)response.StatusCode}");
        }

        allowed.ShouldBeEmpty();
    }
}
