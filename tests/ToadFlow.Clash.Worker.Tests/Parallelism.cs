using TUnit.Core.Interfaces;

[assembly: TUnit.Core.ParallelLimiter<ToadFlow.Clash.Worker.Tests.WorkerTestsLimit>]

namespace ToadFlow.Clash.Worker.Tests;

/// <summary>Тесты поднимают сервер и свою базу; больше 4 одновременно только замедляют прогон.</summary>
public sealed class WorkerTestsLimit : IParallelLimit
{
    public int Limit => 4;
}
