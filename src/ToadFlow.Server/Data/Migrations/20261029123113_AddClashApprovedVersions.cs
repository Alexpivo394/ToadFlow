using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ToadFlow.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddClashApprovedVersions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "approved_run_id",
                table: "clash_parts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "approved_version",
                table: "clash_parts",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "next_collision_number",
                table: "clash_parts",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.CreateTable(
                name: "clash_part_versions",
                columns: table => new
                {
                    part_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    configuration_json = table.Column<string>(type: "jsonb", nullable: false),
                    comment = table.Column<string>(type: "text", nullable: true),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    approved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_clash_part_versions", x => new { x.part_id, x.version });
                    table.ForeignKey(
                        name: "fk_clash_part_versions_clash_parts_part_id",
                        column: x => x.part_id,
                        principalTable: "clash_parts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "clash_part_versions");

            migrationBuilder.DropColumn(
                name: "approved_run_id",
                table: "clash_parts");

            migrationBuilder.DropColumn(
                name: "approved_version",
                table: "clash_parts");

            migrationBuilder.DropColumn(
                name: "next_collision_number",
                table: "clash_parts");
        }
    }
}
