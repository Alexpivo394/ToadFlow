using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ToadFlow.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class FamilyIterations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "iteration_id",
                table: "family_versions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "silent",
                table: "family_versions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "final_comment",
                table: "family_publications",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "iteration_comment",
                table: "family_publications",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "iteration_id",
                table: "family_publications",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "silent",
                table: "family_publications",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "iteration_id",
                table: "family_loads",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "coordinators_only",
                table: "family_cards",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "silent",
                table: "family_cards",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "family_iterations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    card_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    name_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    comment = table.Column<string>(type: "text", nullable: true),
                    current_version_id = table.Column<Guid>(type: "uuid", nullable: true),
                    relative_path = table.Column<string>(type: "text", nullable: false),
                    coordinators_only = table.Column<bool>(type: "boolean", nullable: false),
                    final_version_id = table.Column<Guid>(type: "uuid", nullable: true),
                    final_comment = table.Column<string>(type: "text", nullable: true),
                    final_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    final_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    removed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_family_iterations", x => x.id);
                    table.ForeignKey(
                        name: "fk_family_iterations_family_cards_card_id",
                        column: x => x.card_id,
                        principalTable: "family_cards",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_family_versions_iteration_id",
                table: "family_versions",
                column: "iteration_id");

            migrationBuilder.CreateIndex(
                name: "ix_family_iterations_card_id_name_key",
                table: "family_iterations",
                columns: new[] { "card_id", "name_key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "family_iterations");

            migrationBuilder.DropIndex(
                name: "ix_family_versions_iteration_id",
                table: "family_versions");

            migrationBuilder.DropColumn(
                name: "iteration_id",
                table: "family_versions");

            migrationBuilder.DropColumn(
                name: "silent",
                table: "family_versions");

            migrationBuilder.DropColumn(
                name: "final_comment",
                table: "family_publications");

            migrationBuilder.DropColumn(
                name: "iteration_comment",
                table: "family_publications");

            migrationBuilder.DropColumn(
                name: "iteration_id",
                table: "family_publications");

            migrationBuilder.DropColumn(
                name: "silent",
                table: "family_publications");

            migrationBuilder.DropColumn(
                name: "iteration_id",
                table: "family_loads");

            migrationBuilder.DropColumn(
                name: "coordinators_only",
                table: "family_cards");

            migrationBuilder.DropColumn(
                name: "silent",
                table: "family_cards");
        }
    }
}
