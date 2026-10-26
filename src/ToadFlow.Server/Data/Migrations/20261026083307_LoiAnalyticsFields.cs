using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ToadFlow.Server.Data.Migrations
{
    /// <summary>Свои поля справочника именования (например «Стадия») — отдельным представлением для Power BI: строка — поле проверки.</summary>
    public partial class LoiAnalyticsFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE VIEW loi_analytics_fields AS
                SELECT c.id AS check_id, f.key AS field, f.value AS value
                FROM loi_checks c
                CROSS JOIN LATERAL jsonb_each_text(c.fields) AS f;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP VIEW IF EXISTS loi_analytics_fields;");
        }
    }
}
