using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ToadFlow.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddClashParts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "clash_parts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    @object = table.Column<string>(name: "object", type: "text", nullable: true),
                    part = table.Column<string>(type: "text", nullable: true),
                    draft_json = table.Column<string>(type: "jsonb", nullable: false),
                    updated_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_clash_parts", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "clash_part_models",
                columns: table => new
                {
                    part_id = table.Column<Guid>(type: "uuid", nullable: false),
                    model_id = table.Column<Guid>(type: "uuid", nullable: false),
                    discipline_override = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_clash_part_models", x => new { x.part_id, x.model_id });
                    table.ForeignKey(
                        name: "fk_clash_part_models_clash_models_model_id",
                        column: x => x.model_id,
                        principalTable: "clash_models",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_clash_part_models_clash_parts_part_id",
                        column: x => x.part_id,
                        principalTable: "clash_parts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_clash_part_models_model_id",
                table: "clash_part_models",
                column: "model_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "clash_part_models");

            migrationBuilder.DropTable(
                name: "clash_parts");
        }
    }
}
