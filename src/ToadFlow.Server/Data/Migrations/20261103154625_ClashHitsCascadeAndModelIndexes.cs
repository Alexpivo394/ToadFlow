using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ToadFlow.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class ClashHitsCascadeAndModelIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Находки удалённых частей и прогонов оставались без владельца: внешний ключ на них не встал бы.
            migrationBuilder.Sql("DELETE FROM clash_draft_hits h WHERE NOT EXISTS (SELECT 1 FROM clash_parts p WHERE p.id = h.part_id)");
            migrationBuilder.Sql("DELETE FROM clash_approved_hits h WHERE NOT EXISTS (SELECT 1 FROM clash_engine_runs r WHERE r.id = h.run_id)");

            migrationBuilder.CreateIndex(
                name: "ix_clash_collisions_model_a",
                table: "clash_collisions",
                column: "model_a");

            migrationBuilder.CreateIndex(
                name: "ix_clash_collisions_model_b",
                table: "clash_collisions",
                column: "model_b");

            migrationBuilder.AddForeignKey(
                name: "fk_clash_approved_hits_clash_engine_runs_run_id",
                table: "clash_approved_hits",
                column: "run_id",
                principalTable: "clash_engine_runs",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_clash_draft_hits_clash_parts_part_id",
                table: "clash_draft_hits",
                column: "part_id",
                principalTable: "clash_parts",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_clash_approved_hits_clash_engine_runs_run_id",
                table: "clash_approved_hits");

            migrationBuilder.DropForeignKey(
                name: "fk_clash_draft_hits_clash_parts_part_id",
                table: "clash_draft_hits");

            migrationBuilder.DropIndex(
                name: "ix_clash_collisions_model_a",
                table: "clash_collisions");

            migrationBuilder.DropIndex(
                name: "ix_clash_collisions_model_b",
                table: "clash_collisions");
        }
    }
}
