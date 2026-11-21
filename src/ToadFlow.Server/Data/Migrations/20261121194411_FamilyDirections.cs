using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ToadFlow.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class FamilyDirections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "direction_id",
                table: "family_requests",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "family_directions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    sections = table.Column<string[]>(type: "text[]", nullable: false),
                    disciplines = table.Column<string[]>(type: "text[]", nullable: false),
                    coordinators = table.Column<Guid[]>(type: "uuid[]", nullable: false),
                    hint = table.Column<string>(type: "text", nullable: false),
                    order = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_family_directions", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_family_requests_direction_id",
                table: "family_requests",
                column: "direction_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "family_directions");

            migrationBuilder.DropIndex(
                name: "ix_family_requests_direction_id",
                table: "family_requests");

            migrationBuilder.DropColumn(
                name: "direction_id",
                table: "family_requests");
        }
    }
}
