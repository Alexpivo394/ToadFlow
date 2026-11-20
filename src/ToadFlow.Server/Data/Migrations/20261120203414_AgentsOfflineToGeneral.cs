using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ToadFlow.Server.Data.Migrations
{
    /// <summary>
    ///     Порог «агенты не в сети» из настроек коллизий переезжает в «Общие»: он один на сервер. Значение в настройках коллизий
    ///     остаётся (миграция ничего не удаляет) — коллизии его больше не читают.
    /// </summary>
    public partial class AgentsOfflineToGeneral : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE settings AS g
                SET value = jsonb_set(g.value::jsonb, '{agentsOfflineMinutes}', c.value::jsonb -> 'agentsOfflineMinutes')::text
                FROM settings AS c
                WHERE g.key = 'general' AND c.key = 'clash.overview' AND c.value::jsonb ? 'agentsOfflineMinutes';

                INSERT INTO settings (key, value)
                SELECT 'general', jsonb_build_object('agentsOfflineMinutes', c.value::jsonb -> 'agentsOfflineMinutes')::text
                FROM settings AS c
                WHERE c.key = 'clash.overview' AND c.value::jsonb ? 'agentsOfflineMinutes'
                  AND NOT EXISTS (SELECT 1 FROM settings WHERE key = 'general');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
