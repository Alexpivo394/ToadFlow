using System;
using Microsoft.EntityFrameworkCore.Migrations;
using ToadFlow.Server.Data;

#nullable disable

namespace ToadFlow.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class ClashCollisionSeen : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "opened_at",
                table: "clash_collisions",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.CreateTable(
                name: "clash_collision_seen",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    collision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_clash_collision_seen", x => new { x.user_id, x.collision_id });
                    table.ForeignKey(
                        name: "fk_clash_collision_seen_clash_collisions_collision_id",
                        column: x => x.collision_id,
                        principalTable: "clash_collisions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_clash_collision_seen_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_clash_collision_seen_collision_id",
                table: "clash_collision_seen",
                column: "collision_id");

            migrationBuilder.Sql(ClashSeenBackfill.Sql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "clash_collision_seen");

            migrationBuilder.DropColumn(
                name: "opened_at",
                table: "clash_collisions");
        }
    }
}
