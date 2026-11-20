using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ToadFlow.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class RunModuleName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "module_name",
                table: "runs",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_runs_module_name_created_at",
                table: "runs",
                columns: new[] { "module_name", "created_at" });

            // Модуль прежних запусков: по самому раннему заданию, а где заданий нет — по расписанию. Соответствие видов модулям —
            // на момент миграции (IServerModule.Kinds); у миграции оно не должно меняться вместе с кодом.
            const string module = """
                CASE {0}
                    WHEN 'export' THEN 'export' WHEN '' THEN 'export'
                    WHEN 'loi' THEN 'loi' WHEN 'loi-catalog' THEN 'loi'
                    WHEN 'clash-extract' THEN 'clash'
                END
                """;
            migrationBuilder.Sql($"""
                UPDATE runs AS r SET module_name = {string.Format(module, "j.module_kind")}
                FROM (SELECT DISTINCT ON (run_id) run_id, module_kind FROM jobs ORDER BY run_id, created_at, id) AS j
                WHERE j.run_id = r.id;
                """);
            migrationBuilder.Sql($"""
                UPDATE runs AS r SET module_name = {string.Format(module, "s.module_kind")}
                FROM schedules AS s
                WHERE r.module_name IS NULL AND r.schedule_id = s.id;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_runs_module_name_created_at",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "module_name",
                table: "runs");
        }
    }
}
