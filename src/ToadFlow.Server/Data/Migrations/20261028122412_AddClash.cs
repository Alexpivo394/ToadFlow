using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ToadFlow.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddClash : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "clash_models",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    model_path = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_clash_models", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "clash_uploads",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    agent_id = table.Column<Guid>(type: "uuid", nullable: false),
                    model_id = table.Column<Guid>(type: "uuid", nullable: false),
                    geometry_bytes = table.Column<long>(type: "bigint", nullable: false),
                    geometry_sha256 = table.Column<string>(type: "text", nullable: false),
                    parameters_bytes = table.Column<long>(type: "bigint", nullable: false),
                    parameters_sha256 = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    snapshot_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_clash_uploads", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "clash_snapshots",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    model_id = table.Column<Guid>(type: "uuid", nullable: false),
                    job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    extracted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revit_year = table.Column<int>(type: "integer", nullable: false),
                    view_name = table.Column<string>(type: "text", nullable: false),
                    elements = table.Column<int>(type: "integer", nullable: false),
                    triangles = table.Column<long>(type: "bigint", nullable: false),
                    inventory = table.Column<int>(type: "integer", nullable: false),
                    geometry_bytes = table.Column<long>(type: "bigint", nullable: false),
                    parameters_bytes = table.Column<long>(type: "bigint", nullable: false),
                    catalog = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_clash_snapshots", x => x.id);
                    table.ForeignKey(
                        name: "fk_clash_snapshots_clash_models_model_id",
                        column: x => x.model_id,
                        principalTable: "clash_models",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_clash_models_key",
                table: "clash_models",
                column: "key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_clash_snapshots_model_id_extracted_at",
                table: "clash_snapshots",
                columns: new[] { "model_id", "extracted_at" });

            migrationBuilder.CreateIndex(
                name: "ix_clash_uploads_created_at",
                table: "clash_uploads",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_clash_uploads_job_id",
                table: "clash_uploads",
                column: "job_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "clash_snapshots");

            migrationBuilder.DropTable(
                name: "clash_uploads");

            migrationBuilder.DropTable(
                name: "clash_models");
        }
    }
}
