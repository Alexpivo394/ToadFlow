using System.Reflection;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;

namespace ToadFlow.Server.Tests.Infrastructure;

/// <summary>
///     Гасит ограничители частоты остановленного сервера. <c>UseRateLimiter</c> создаёт ограничитель с вечным таймером и при остановке
///     его не освобождает; таймер через контекст выполнения держит фабрику сервера, а с ней — весь остановленный сервер. В бою сервер
///     один, а в тестах так копились сотни серверов: гигабайты памяти и прогон, замедляющийся к концу в разы. Открытого доступа к
///     ограничителю нет, поэтому он ищется по цепочке обработчиков запроса.
/// </summary>
public static class RateLimiterCleanup
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
    private const int MaxObjects = 500;

    /// <summary>Ограничители в цепочке обработчиков сервера; искать нужно до его остановки.</summary>
    public static IReadOnlyList<PartitionedRateLimiter<HttpContext>> Find(object server)
    {
        var found = new List<PartitionedRateLimiter<HttpContext>>();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var queue = new Queue<object>();
        queue.Enqueue(server);
        while (queue.Count > 0 && seen.Count < MaxObjects)
        {
            var current = queue.Dequeue();
            if (!seen.Add(current)) continue;
            var isLimiterMiddleware = current.GetType().Name == "RateLimitingMiddleware";
            for (var type = current.GetType(); type is not null && type != typeof(object); type = type.BaseType)
            {
                foreach (var field in type.GetFields(Fields))
                {
                    switch (field.GetValue(current))
                    {
                        case PartitionedRateLimiter<HttpContext> limiter when isLimiterMiddleware:
                            found.Add(limiter);
                            break;
                        case Delegate handler:
                            foreach (var target in handler.GetInvocationList().Select(d => d.Target).OfType<object>()) queue.Enqueue(target);
                            break;
                        case { } value when IsApplication(value.GetType()):
                            queue.Enqueue(value);
                            break;
                    }
                }
            }
        }

        return found;
    }

    /// <summary>Обёртки между сервером тестов и первым обработчиком запроса.</summary>
    private static bool IsApplication(Type type) =>
        type.FullName is { } name && (name.StartsWith("Microsoft.AspNetCore.TestHost.ApplicationWrapper", StringComparison.Ordinal)
                                      || name == "Microsoft.AspNetCore.Hosting.HostingApplication");
}
