using TUnit.Core.Interfaces;

[assembly: TUnit.Core.ParallelLimiter<ToadFlow.Agent.Tests.Infrastructure.AgentTestsLimit>]

namespace ToadFlow.Agent.Tests.Infrastructure;

/// <summary>Интеграционные тесты поднимают сервер и свою базу; больше 4 одновременно только замедляют прогон.</summary>
public sealed class AgentTestsLimit : IParallelLimit
{
    public int Limit => 4;
}
