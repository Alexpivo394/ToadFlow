using System.Runtime.CompilerServices;
using Shouldly;

namespace ToadFlow.Clash.Worker.Tests;

/// <summary>Работник между прогонами ничего не держит в памяти: всё, что занял прогон, отдаётся системе.</summary>
[NotInParallel]
public class WorkerMemoryTests
{
    [Test]
    public void ReleaseMemory_AfterRun_WorkingSetReturned()
    {
        var during = Run(512);

        // Прогон кончился, ссылок нет, но сборки в простое не будет: память возвращает только явная сборка.
        EngineWorker.ReleaseMemory();

        Environment.WorkingSet.ShouldBeLessThan(during - (256L << 20));
    }

    /// <summary>«Прогон»: <paramref name="mb" /> МБ данных; возвращает занятую процессом память, пока они живы.</summary>
    /// <remarks>Отдельный метод: в отладочной сборке локальная ссылка жила бы до конца теста.</remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long Run(int mb)
    {
        var chunks = new byte[mb][];
        for (var i = 0; i < mb; i++)
        {
            chunks[i] = new byte[1 << 20];
            Array.Fill(chunks[i], (byte)1);
        }

        var workingSet = Environment.WorkingSet;
        GC.KeepAlive(chunks);
        return workingSet;
    }
}
