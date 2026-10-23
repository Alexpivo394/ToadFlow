using Microsoft.Extensions.Time.Testing;

namespace ToadFlow.Agent.Tests.Infrastructure;

public static class Clock
{
    /// <summary>
    ///     Двигает подменное время шагами, пока задача не завершится: таймауты агента ждут по <see cref="TimeProvider" />,
    ///     а пайп и имитация аддина работают в настоящем времени.
    /// </summary>
    public static async Task<T> DriveAsync<T>(this FakeTimeProvider time, Task<T> task, TimeSpan step, TimeSpan limit)
    {
        var passed = TimeSpan.Zero;
        while (!task.IsCompleted)
        {
            if (passed > limit) throw new TimeoutException($"Задача не завершилась за {limit} подменного времени");
            await Task.Delay(25);
            time.Advance(step);
            passed += step;
        }

        return await task;
    }

    public static async Task DriveAsync(this FakeTimeProvider time, Task task, TimeSpan step, TimeSpan limit) =>
        await time.DriveAsync(task.ContinueWith(t => { t.GetAwaiter().GetResult(); return true; }), step, limit);
}
