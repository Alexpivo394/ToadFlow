using ToadFlow.Agent.Server;

namespace ToadFlow.Agent.Tests.Infrastructure;

/// <summary>Память машины для тестов: по умолчанию её с запасом, чтобы решения агента не зависели от машины, где идут тесты.</summary>
public sealed class FakeMemory : ISystemMemory
{
    public RamSnapshot? Current { get; set; } = new RamSnapshot(65536, 48000);

    public RamSnapshot? Snapshot() => Current;
}
