using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ToadFlow.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class ClashImagesOnDemand : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "clash_image_jobs");

            migrationBuilder.DropColumn(
                name: "has_image",
                table: "clash_collisions");

            migrationBuilder.DropColumn(
                name: "image_fingerprint_a",
                table: "clash_collisions");

            migrationBuilder.DropColumn(
                name: "image_fingerprint_b",
                table: "clash_collisions");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "has_image",
                table: "clash_collisions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "image_fingerprint_a",
                table: "clash_collisions",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "image_fingerprint_b",
                table: "clash_collisions",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "clash_image_jobs",
                columns: table => new
                {
                    part_id = table.Column<Guid>(type: "uuid", nullable: false),
                    attempt = table.Column<int>(type: "integer", nullable: false),
                    error = table.Column<string>(type: "text", nullable: true),
                    lease_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    run_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_clash_image_jobs", x => x.part_id);
                    table.ForeignKey(
                        name: "fk_clash_image_jobs_clash_parts_part_id",
                        column: x => x.part_id,
                        principalTable: "clash_parts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });
        }
    }
}
