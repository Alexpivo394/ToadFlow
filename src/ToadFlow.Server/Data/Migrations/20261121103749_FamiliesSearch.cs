using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ToadFlow.Server.Data.Migrations
{
    /// <summary>
    ///     Индекс поиска семейств по части слова: триграммы (<c>pg_trgm</c>, GIN по <c>search_text</c>) — под <c>LIKE '%…%'</c>. Расширение
    ///     ставится, только если оно есть у сервера PostgreSQL; без него поиск работает так же, только без индекса.
    /// </summary>
    public partial class FamiliesSearch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_available_extensions WHERE name = 'pg_trgm') THEN
                        CREATE EXTENSION IF NOT EXISTS pg_trgm;
                        CREATE INDEX IF NOT EXISTS ix_family_cards_search_text_trgm ON family_cards USING gin (search_text gin_trgm_ops);
                    END IF;
                END
                $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS ix_family_cards_search_text_trgm;");
        }
    }
}
