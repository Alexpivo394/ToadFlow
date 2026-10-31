using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ToadFlow.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class ClashImagesAndRunFeed : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "first_run_id",
                table: "clash_collisions",
                type: "uuid",
                nullable: true);

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

            migrationBuilder.AddColumn<Guid>(
                name: "run_id",
                table: "clash_collision_events",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "clash_image_jobs",
                columns: table => new
                {
                    part_id = table.Column<Guid>(type: "uuid", nullable: false),
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    lease_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    attempt = table.Column<int>(type: "integer", nullable: false),
                    error = table.Column<string>(type: "text", nullable: true)
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

            migrationBuilder.CreateIndex(
                name: "ix_clash_collisions_part_id_last_found_run_id",
                table: "clash_collisions",
                columns: new[] { "part_id", "last_found_run_id" });

            migrationBuilder.CreateIndex(
                name: "ix_clash_collision_events_run_id",
                table: "clash_collision_events",
                column: "run_id",
                filter: "run_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "clash_image_jobs");

            migrationBuilder.DropIndex(
                name: "ix_clash_collisions_part_id_last_found_run_id",
                table: "clash_collisions");

            migrationBuilder.DropIndex(
                name: "ix_clash_collision_events_run_id",
                table: "clash_collision_events");

            migrationBuilder.DropColumn(
                name: "first_run_id",
                table: "clash_collisions");

            migrationBuilder.DropColumn(
                name: "has_image",
                table: "clash_collisions");

            migrationBuilder.DropColumn(
                name: "image_fingerprint_a",
                table: "clash_collisions");

            migrationBuilder.DropColumn(
                name: "image_fingerprint_b",
                table: "clash_collisions");

            migrationBuilder.DropColumn(
                name: "run_id",
                table: "clash_collision_events");
        }
    }
}
