using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ToadFlow.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddClashScheduledApprovedRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_clash_engine_runs_part_id_trigger_run_id",
                table: "clash_engine_runs",
                columns: new[] { "part_id", "trigger_run_id" },
                unique: true,
                filter: "trigger_run_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_clash_engine_runs_part_id_trigger_run_id",
                table: "clash_engine_runs");
        }
    }
}
