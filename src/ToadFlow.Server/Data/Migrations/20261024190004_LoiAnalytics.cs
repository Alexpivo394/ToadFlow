using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ToadFlow.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class LoiAnalytics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "api_tokens",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    prefix = table.Column<string>(type: "text", nullable: false),
                    hash = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_api_tokens", x => x.id);
                    table.ForeignKey(
                        name: "fk_api_tokens_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "loi_checks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    checked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    schedule_id = table.Column<Guid>(type: "uuid", nullable: true),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    model_key = table.Column<string>(type: "text", nullable: false),
                    model_name = table.Column<string>(type: "text", nullable: false),
                    model_path = table.Column<string>(type: "text", nullable: false),
                    customer = table.Column<string>(type: "text", nullable: true),
                    @object = table.Column<string>(name: "object", type: "text", nullable: true),
                    discipline = table.Column<string>(type: "text", nullable: true),
                    part = table.Column<string>(type: "text", nullable: true),
                    fields = table.Column<string>(type: "jsonb", nullable: false),
                    profile_name = table.Column<string>(type: "text", nullable: false),
                    profile_fingerprint = table.Column<string>(type: "text", nullable: false),
                    view_name = table.Column<string>(type: "text", nullable: false),
                    duration_seconds = table.Column<double>(type: "double precision", nullable: false),
                    elements = table.Column<int>(type: "integer", nullable: false),
                    complete_elements = table.Column<int>(type: "integer", nullable: false),
                    values = table.Column<int>(type: "integer", nullable: false),
                    filled = table.Column<int>(type: "integer", nullable: false),
                    empty = table.Column<int>(type: "integer", nullable: false),
                    missing = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_loi_checks", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "loi_model_overrides",
                columns: table => new
                {
                    model_key = table.Column<string>(type: "text", nullable: false),
                    model_name = table.Column<string>(type: "text", nullable: false),
                    customer = table.Column<string>(type: "text", nullable: true),
                    @object = table.Column<string>(name: "object", type: "text", nullable: true),
                    discipline = table.Column<string>(type: "text", nullable: true),
                    part = table.Column<string>(type: "text", nullable: true),
                    updated_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_loi_model_overrides", x => x.model_key);
                });

            migrationBuilder.CreateTable(
                name: "loi_check_rules",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    check_id = table.Column<Guid>(type: "uuid", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    elements = table.Column<int>(type: "integer", nullable: false),
                    complete_elements = table.Column<int>(type: "integer", nullable: false),
                    values = table.Column<int>(type: "integer", nullable: false),
                    filled = table.Column<int>(type: "integer", nullable: false),
                    empty = table.Column<int>(type: "integer", nullable: false),
                    missing = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_loi_check_rules", x => x.id);
                    table.ForeignKey(
                        name: "fk_loi_check_rules_loi_checks_check_id",
                        column: x => x.check_id,
                        principalTable: "loi_checks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "loi_check_parameters",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    rule_id = table.Column<long>(type: "bigint", nullable: false),
                    check_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    filled = table.Column<int>(type: "integer", nullable: false),
                    empty = table.Column<int>(type: "integer", nullable: false),
                    missing = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_loi_check_parameters", x => x.id);
                    table.ForeignKey(
                        name: "fk_loi_check_parameters_loi_check_rules_rule_id",
                        column: x => x.rule_id,
                        principalTable: "loi_check_rules",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_api_tokens_hash",
                table: "api_tokens",
                column: "hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_api_tokens_user_id",
                table: "api_tokens",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_loi_check_parameters_check_id_name",
                table: "loi_check_parameters",
                columns: new[] { "check_id", "name" });

            migrationBuilder.CreateIndex(
                name: "ix_loi_check_parameters_name",
                table: "loi_check_parameters",
                column: "name");

            migrationBuilder.CreateIndex(
                name: "ix_loi_check_parameters_rule_id",
                table: "loi_check_parameters",
                column: "rule_id");

            migrationBuilder.CreateIndex(
                name: "ix_loi_check_rules_check_id",
                table: "loi_check_rules",
                column: "check_id");

            migrationBuilder.CreateIndex(
                name: "ix_loi_checks_checked_at",
                table: "loi_checks",
                column: "checked_at");

            migrationBuilder.CreateIndex(
                name: "ix_loi_checks_model_key_checked_at",
                table: "loi_checks",
                columns: new[] { "model_key", "checked_at" });

            migrationBuilder.CreateIndex(
                name: "ix_loi_checks_object_discipline_checked_at",
                table: "loi_checks",
                columns: new[] { "object", "discipline", "checked_at" });

            // Плоская таблица для Power BI и подобных: строка — параметр правила в проверке модели, со всеми разрезами.
            migrationBuilder.Sql("""
                CREATE VIEW loi_analytics AS
                SELECT c.id AS check_id, c.checked_at, c.source, c.customer, c.object, c.discipline, c.part, c.model_name, c.model_path,
                       c.profile_name, c.profile_fingerprint, c.view_name,
                       c.elements AS model_elements, c.complete_elements AS model_complete_elements,
                       c."values" AS model_values, c.filled AS model_filled,
                       r.position AS rule_position, r.title AS rule_title, r.elements AS rule_elements, r.complete_elements AS rule_complete_elements,
                       p.name AS parameter, p.filled, p.empty, p.missing, p.filled + p.empty + p.missing AS total
                FROM loi_checks c
                JOIN loi_check_rules r ON r.check_id = c.id
                JOIN loi_check_parameters p ON p.rule_id = r.id;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP VIEW IF EXISTS loi_analytics;");

            migrationBuilder.DropTable(
                name: "api_tokens");

            migrationBuilder.DropTable(
                name: "loi_check_parameters");

            migrationBuilder.DropTable(
                name: "loi_model_overrides");

            migrationBuilder.DropTable(
                name: "loi_check_rules");

            migrationBuilder.DropTable(
                name: "loi_checks");
        }
    }
}
