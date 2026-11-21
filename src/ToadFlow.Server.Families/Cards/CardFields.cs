using Alexpivo394.Revit.Families.Files;
using ToadFlow.Server.Families.Data;
using ToadFlow.Server.Families.Search;

namespace ToadFlow.Server.Families.Cards;

/// <summary>Поля карточки, которые берутся из действующей версии, и текст поиска.</summary>
public static class CardFields
{
    /// <summary>
    ///     Обновляет категорию, размещение, версию Revit, число типоразмеров и текст поиска по действующей версии. Пока ферма не
    ///     прочитала новую версию, содержимое берётся из <paramref name="previousContent" /> (прежняя версия): поиск не теряет семейство.
    /// </summary>
    public static void Refresh(FamilyCard card, FamilyVersion? current, FamilyContent? previousContent = null)
    {
        var content = SearchText.ContentOf(current) ?? previousContent;
        if (content is not null)
        {
            card.Category = content.Category;
            card.Placement = content.Placement;
            card.TypeCount = content.Types.Count;
        }

        card.RevitYear = current?.RevitYear ?? content?.RevitYear ?? card.RevitYear;
        card.SearchText = SearchText.Build(card, content);
    }
}
