using TUnit.Core.Interfaces;

[assembly: TUnit.Core.ParallelLimiter<ToadFlow.Server.Tests.Infrastructure.ServerTestsLimit>]

namespace ToadFlow.Server.Tests.Infrastructure;

/// <summary>Каждый тест поднимает свой сервер и свою базу; больше 4 одновременно только замедляют прогон.</summary>
public sealed class ServerTestsLimit : IParallelLimit
{
    public int Limit => 4;
}
