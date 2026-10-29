using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ToadFlow.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddClashApprovedRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "kind",
                table: "clash_engine_runs",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Draft");

            migrationBuilder.AddColumn<Guid>(
                name: "previous_run_id",
                table: "clash_engine_runs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "summary",
                table: "clash_engine_runs",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "trigger_run_id",
                table: "clash_engine_runs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid[]>(
                name: "unchecked_models",
                table: "clash_engine_runs",
                type: "uuid[]",
                nullable: false,
                defaultValue: new Guid[0]);

            migrationBuilder.AddColumn<int>(
                name: "version",
                table: "clash_engine_runs",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "clash_approved_hits",
                columns: table => new
                {
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<int>(type: "integer", nullable: false),
                    part_id = table.Column<Guid>(type: "uuid", nullable: false),
                    discipline_pair = table.Column<string>(type: "text", nullable: false),
                    checks = table.Column<string[]>(type: "text[]", nullable: false),
                    model_a = table.Column<string>(type: "text", nullable: false),
                    unique_id_a = table.Column<string>(type: "text", nullable: false),
                    element_id_a = table.Column<long>(type: "bigint", nullable: false),
                    discipline_a = table.Column<string>(type: "text", nullable: false),
                    sets_a = table.Column<Guid[]>(type: "uuid[]", nullable: false),
                    category_a = table.Column<string>(type: "text", nullable: false),
                    family_a = table.Column<string>(type: "text", nullable: true),
                    type_a = table.Column<string>(type: "text", nullable: true),
                    level_a = table.Column<string>(type: "text", nullable: true),
                    model_b = table.Column<string>(type: "text", nullable: false),
                    unique_id_b = table.Column<string>(type: "text", nullable: false),
                    element_id_b = table.Column<long>(type: "bigint", nullable: false),
                    discipline_b = table.Column<string>(type: "text", nullable: false),
                    sets_b = table.Column<Guid[]>(type: "uuid[]", nullable: false),
                    category_b = table.Column<string>(type: "text", nullable: false),
                    family_b = table.Column<string>(type: "text", nullable: true),
                    type_b = table.Column<string>(type: "text", nullable: true),
                    level_b = table.Column<string>(type: "text", nullable: true),
                    x = table.Column<double>(type: "double precision", nullable: false),
                    y = table.Column<double>(type: "double precision", nullable: false),
                    z = table.Column<double>(type: "double precision", nullable: false),
                    depth_mm = table.Column<float>(type: "real", nullable: false),
                    fingerprint_a = table.Column<long>(type: "bigint", nullable: false),
                    fingerprint_b = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_clash_approved_hits", x => new { x.run_id, x.number });
                });

            migrationBuilder.CreateIndex(
                name: "ix_clash_engine_runs_one_queued_approved",
                table: "clash_engine_runs",
                column: "part_id",
                unique: true,
                filter: "kind = 'Approved' AND state = 'Queued'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "clash_approved_hits");

            migrationBuilder.DropIndex(
                name: "ix_clash_engine_runs_one_queued_approved",
                table: "clash_engine_runs");

            migrationBuilder.DropColumn(
                name: "kind",
                table: "clash_engine_runs");

            migrationBuilder.DropColumn(
                name: "previous_run_id",
                table: "clash_engine_runs");

            migrationBuilder.DropColumn(
                name: "summary",
                table: "clash_engine_runs");

            migrationBuilder.DropColumn(
                name: "trigger_run_id",
                table: "clash_engine_runs");

            migrationBuilder.DropColumn(
                name: "unchecked_models",
                table: "clash_engine_runs");

            migrationBuilder.DropColumn(
                name: "version",
                table: "clash_engine_runs");
        }
    }
}
