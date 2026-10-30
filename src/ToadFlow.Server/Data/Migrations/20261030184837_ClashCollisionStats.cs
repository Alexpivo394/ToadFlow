using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ToadFlow.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class ClashCollisionStats : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "clash_collision_stats",
                columns: table => new
                {
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    discipline = table.Column<string>(type: "text", nullable: false),
                    part_id = table.Column<Guid>(type: "uuid", nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    open = table.Column<int>(type: "integer", nullable: false),
                    fake = table.Column<int>(type: "integer", nullable: false),
                    @new = table.Column<int>(name: "new", type: "integer", nullable: false),
                    @fixed = table.Column<int>(name: "fixed", type: "integer", nullable: false),
                    returned = table.Column<int>(type: "integer", nullable: false),
                    gone_by_rules = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_clash_collision_stats", x => new { x.run_id, x.discipline });
                    table.ForeignKey(
                        name: "fk_clash_collision_stats_clash_parts_part_id",
                        column: x => x.part_id,
                        principalTable: "clash_parts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_clash_collision_stats_part_id_discipline_at",
                table: "clash_collision_stats",
                columns: new[] { "part_id", "discipline", "at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "clash_collision_stats");
        }
    }
}
