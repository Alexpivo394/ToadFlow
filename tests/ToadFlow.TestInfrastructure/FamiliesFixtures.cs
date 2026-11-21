using ToadFlow.Server.Families.Data;

namespace ToadFlow.Server.Tests.Infrastructure;

/// <summary>Карточки, версии и отчёты обхода библиотеки семейств для тестов.</summary>
public static class FamiliesFixtures
{
    public static FamilyCard Card(string name, string relativePath, CardState state, DateTimeOffset at)
    {
        var card = new FamilyCard { Id = Guid.CreateVersion7(), RelativePath = relativePath, State = state, CreatedAt = at, UpdatedAt = at };
        card.SetName(name);
        return card;
    }
}
