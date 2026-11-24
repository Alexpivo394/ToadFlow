using Microsoft.EntityFrameworkCore;
using ToadFlow.Server.Auth;
using ToadFlow.Server.Data;
using ToadFlow.Server.Overview;
using ToadFlow.Server.Settings;

namespace ToadFlow.Server.Usage.Service;

/// <param name="Space">Пространство веба (<c>clash</c>, <c>farm</c>…).</param>
/// <param name="Page">Пункт меню — адрес без параметров (<c>/clash/checks</c>).</param>
public sealed record PageView(string? Space, string? Page);

public static class PageViewEndpoints
{
    public const int MaxLength = 100;

    /// <summary>
    ///     <c>POST /api/usage/pageview</c> — веб сообщает о переходе на страницу; любая роль, включая проектировщика. +1 в счётчик
    ///     «человек × страница × день». Запрос с личным токеном (не из веба) не считается, но и не отклоняется.
    /// </summary>
    public static void MapUsagePageViews(this RouteGroupBuilder usage) =>
        usage.MapPost("/pageview", async (PageView? body, HttpContext http, ToadFlowDb db, ISettingsStore settings, TimeProvider time, CancellationToken ct) =>
        {
            var space = body?.Space?.Trim() ?? "";
            var page = body?.Page?.Trim() ?? "";
            if (space.Length is 0 or > MaxLength || page.Length is 0 or > MaxLength || !page.StartsWith('/') || page.Contains('?'))
                return Results.BadRequest(new { error = "Нужны пространство и адрес страницы без параметров, до 100 знаков" });
            if (http.User.FindFirst(ApiTokenAuthHandler.TokenClaim) is not null) return Results.NoContent();

            var day = OverviewDays.DayOf(time.GetUtcNow(), (await settings.GetAsync(ct)).TimeZone);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO usage_web_days (user_id, day, space, page, count) VALUES ({http.User.UserId()}, {day}, {space}, {page}, 1)
                ON CONFLICT (user_id, day, space, page) DO UPDATE SET count = usage_web_days.count + 1
                """, ct);
            return Results.NoContent();
        });
}
