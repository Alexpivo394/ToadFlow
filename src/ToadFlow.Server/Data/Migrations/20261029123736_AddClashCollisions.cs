using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ToadFlow.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddClashCollisions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "clash_collisions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    part_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    assignee = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    found_now = table.Column<bool>(type: "boolean", nullable: false),
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
                    discipline_pair = table.Column<string>(type: "text", nullable: false),
                    checks = table.Column<string[]>(type: "text[]", nullable: false),
                    x = table.Column<double>(type: "double precision", nullable: false),
                    y = table.Column<double>(type: "double precision", nullable: false),
                    z = table.Column<double>(type: "double precision", nullable: false),
                    depth_mm = table.Column<float>(type: "real", nullable: false),
                    fingerprint_a = table.Column<long>(type: "bigint", nullable: false),
                    fingerprint_b = table.Column<long>(type: "bigint", nullable: false),
                    first_version = table.Column<int>(type: "integer", nullable: false),
                    last_found_version = table.Column<int>(type: "integer", nullable: false),
                    last_found_run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    suggested_predecessor_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_clash_collisions", x => x.id);
                    table.ForeignKey(
                        name: "fk_clash_collisions_clash_parts_part_id",
                        column: x => x.part_id,
                        principalTable: "clash_parts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "clash_collision_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    collision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    text = table.Column<string>(type: "text", nullable: false),
                    from_status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    to_status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    version = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_clash_collision_events", x => x.id);
                    table.ForeignKey(
                        name: "fk_clash_collision_events_clash_collisions_collision_id",
                        column: x => x.collision_id,
                        principalTable: "clash_collisions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "clash_collision_samples",
                columns: table => new
                {
                    collision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    depth_mm = table.Column<float>(type: "real", nullable: false),
                    x = table.Column<double>(type: "double precision", nullable: false),
                    y = table.Column<double>(type: "double precision", nullable: false),
                    z = table.Column<double>(type: "double precision", nullable: false),
                    fingerprint_a = table.Column<long>(type: "bigint", nullable: false),
                    fingerprint_b = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_clash_collision_samples", x => new { x.collision_id, x.run_id });
                    table.ForeignKey(
                        name: "fk_clash_collision_samples_clash_collisions_collision_id",
                        column: x => x.collision_id,
                        principalTable: "clash_collisions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_clash_collision_events_collision_id_at",
                table: "clash_collision_events",
                columns: new[] { "collision_id", "at" });

            migrationBuilder.CreateIndex(
                name: "ix_clash_collisions_checks",
                table: "clash_collisions",
                column: "checks")
                .Annotation("Npgsql:IndexMethod", "gin");

            migrationBuilder.CreateIndex(
                name: "ix_clash_collisions_part_id_model_a_unique_id_a_model_b_unique",
                table: "clash_collisions",
                columns: new[] { "part_id", "model_a", "unique_id_a", "model_b", "unique_id_b" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_clash_collisions_part_id_number",
                table: "clash_collisions",
                columns: new[] { "part_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_clash_collisions_part_id_status",
                table: "clash_collisions",
                columns: new[] { "part_id", "status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "clash_collision_events");

            migrationBuilder.DropTable(
                name: "clash_collision_samples");

            migrationBuilder.DropTable(
                name: "clash_collisions");
        }
    }
}
