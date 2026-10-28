using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ToadFlow.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddClashEngineRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "result_run_id",
                table: "clash_parts",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "clash_draft_hits",
                columns: table => new
                {
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<int>(type: "integer", nullable: false),
                    part_id = table.Column<Guid>(type: "uuid", nullable: false),
                    discipline_pair = table.Column<string>(type: "text", nullable: false),
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
                    depth_mm = table.Column<float>(type: "real", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_clash_draft_hits", x => new { x.run_id, x.number });
                });

            migrationBuilder.CreateTable(
                name: "clash_engine_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    part_id = table.Column<Guid>(type: "uuid", nullable: false),
                    state = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    configuration_json = table.Column<string>(type: "jsonb", nullable: false),
                    models = table.Column<string>(type: "jsonb", nullable: false),
                    attempt = table.Column<int>(type: "integer", nullable: false),
                    max_attempts = table.Column<int>(type: "integer", nullable: false),
                    lease_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    stage = table.Column<string>(type: "text", nullable: true),
                    error = table.Column<string>(type: "text", nullable: true),
                    stats = table.Column<string>(type: "jsonb", nullable: true),
                    hit_count = table.Column<int>(type: "integer", nullable: true),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_clash_engine_runs", x => x.id);
                    table.ForeignKey(
                        name: "fk_clash_engine_runs_clash_parts_part_id",
                        column: x => x.part_id,
                        principalTable: "clash_parts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_clash_draft_hits_part_id",
                table: "clash_draft_hits",
                column: "part_id");

            migrationBuilder.CreateIndex(
                name: "ix_clash_draft_hits_part_id_discipline_pair",
                table: "clash_draft_hits",
                columns: new[] { "part_id", "discipline_pair" });

            migrationBuilder.CreateIndex(
                name: "ix_clash_engine_runs_part_id_created_at",
                table: "clash_engine_runs",
                columns: new[] { "part_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_clash_engine_runs_state_created_at",
                table: "clash_engine_runs",
                columns: new[] { "state", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "clash_draft_hits");

            migrationBuilder.DropTable(
                name: "clash_engine_runs");

            migrationBuilder.DropColumn(
                name: "result_run_id",
                table: "clash_parts");
        }
    }
}
