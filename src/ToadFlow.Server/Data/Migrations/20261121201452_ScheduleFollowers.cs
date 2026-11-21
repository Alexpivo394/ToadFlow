using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ToadFlow.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class ScheduleFollowers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "schedule_followers",
                columns: table => new
                {
                    schedule_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_schedule_followers", x => new { x.schedule_id, x.user_id });
                    table.ForeignKey(
                        name: "fk_schedule_followers_schedules_schedule_id",
                        column: x => x.schedule_id,
                        principalTable: "schedules",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_schedule_followers_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_schedule_followers_user_id",
                table: "schedule_followers",
                column: "user_id");

            // Создатель уже существующего расписания следит за ним — как и у новых.
            migrationBuilder.Sql("INSERT INTO schedule_followers (schedule_id, user_id) SELECT s.id, s.created_by_user_id FROM schedules s JOIN users u ON u.id = s.created_by_user_id;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "schedule_followers");
        }
    }
}
