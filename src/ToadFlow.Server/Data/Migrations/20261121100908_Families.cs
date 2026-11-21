using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ToadFlow.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class Families : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "family_cards",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(260)", maxLength: 260, nullable: false),
                    name_key = table.Column<string>(type: "character varying(260)", maxLength: 260, nullable: false),
                    relative_path = table.Column<string>(type: "text", nullable: false),
                    section = table.Column<string>(type: "text", nullable: false),
                    state = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    current_version_id = table.Column<Guid>(type: "uuid", nullable: true),
                    description = table.Column<string>(type: "text", nullable: true),
                    tags = table.Column<string[]>(type: "text[]", nullable: false, defaultValueSql: "'{}'::text[]"),
                    manufacturer = table.Column<string>(type: "text", nullable: true),
                    category = table.Column<string>(type: "text", nullable: false),
                    placement = table.Column<string>(type: "text", nullable: false),
                    revit_year = table.Column<int>(type: "integer", nullable: true),
                    type_count = table.Column<int>(type: "integer", nullable: false),
                    search_text = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    archived_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_family_cards", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "family_issues",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    card_id = table.Column<Guid>(type: "uuid", nullable: true),
                    relative_path = table.Column<string>(type: "text", nullable: false),
                    hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    text = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_family_issues", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "family_scans",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    agent_id = table.Column<Guid>(type: "uuid", nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    complete = table.Column<bool>(type: "boolean", nullable: true),
                    files = table.Column<int>(type: "integer", nullable: false),
                    errors = table.Column<string[]>(type: "text[]", nullable: false, defaultValueSql: "'{}'::text[]")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_family_scans", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "family_versions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    card_id = table.Column<Guid>(type: "uuid", nullable: false),
                    hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    catalog_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    size = table.Column<long>(type: "bigint", nullable: false),
                    revit_year = table.Column<int>(type: "integer", nullable: true),
                    state = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    relative_path = table.Column<string>(type: "text", nullable: false),
                    note = table.Column<string>(type: "text", nullable: false),
                    author_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    comment = table.Column<string>(type: "text", nullable: true),
                    read_state = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    read_error = table.Column<string>(type: "text", nullable: true),
                    content_json = table.Column<string>(type: "jsonb", nullable: true),
                    has_preview = table.Column<bool>(type: "boolean", nullable: false),
                    seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_family_versions", x => x.id);
                    table.ForeignKey(
                        name: "fk_family_versions_family_cards_card_id",
                        column: x => x.card_id,
                        principalTable: "family_cards",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_family_cards_name_key",
                table: "family_cards",
                column: "name_key",
                unique: true,
                filter: "state <> 'Archived'");

            migrationBuilder.CreateIndex(
                name: "ix_family_cards_state_updated_at",
                table: "family_cards",
                columns: new[] { "state", "updated_at" });

            migrationBuilder.CreateIndex(
                name: "ix_family_issues_resolved_at",
                table: "family_issues",
                column: "resolved_at");

            migrationBuilder.CreateIndex(
                name: "ix_family_scans_started_at",
                table: "family_scans",
                column: "started_at");

            migrationBuilder.CreateIndex(
                name: "ix_family_versions_card_id_seen_at",
                table: "family_versions",
                columns: new[] { "card_id", "seen_at" });

            migrationBuilder.CreateIndex(
                name: "ix_family_versions_hash",
                table: "family_versions",
                column: "hash");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "family_issues");

            migrationBuilder.DropTable(
                name: "family_scans");

            migrationBuilder.DropTable(
                name: "family_versions");

            migrationBuilder.DropTable(
                name: "family_cards");
        }
    }
}
