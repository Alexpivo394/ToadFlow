using TUnit.Core.Interfaces;

[assembly: TUnit.Core.ParallelLimiter<ToadFlow.Server.Tests.Infrastructure.ServerTestsLimit>]

namespace ToadFlow.Server.Tests.Infrastructure;

/// <summary>
///     Каждый тест поднимает свой сервер: старт сервера упирается в один поток (компиляция обработчиков маршрутов), поэтому больше
///     восьми тестов одновременно прогон не ускоряют, а на машине с малым числом ядер хватает четырёх.
/// </summary>
public sealed class ServerTestsLimit : IParallelLimit
{
    public int Limit => Math.Clamp(Environment.ProcessorCount / 2, 4, 8);
}
