using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ToadFlow.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class FamiliesPanelAndRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "catalog_hash",
                table: "family_issues",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "dismissed",
                table: "family_issues",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "resolution",
                table: "family_issues",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "revit_year",
                table: "family_issues",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "size",
                table: "family_issues",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_loaded_at",
                table: "family_cards",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "load_count",
                table: "family_cards",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "family_loads",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    card_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_id = table.Column<Guid>(type: "uuid", nullable: true),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project = table.Column<string>(type: "character varying(260)", maxLength: 260, nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_family_loads", x => x.id);
                    table.ForeignKey(
                        name: "fk_family_loads_family_cards_card_id",
                        column: x => x.card_id,
                        principalTable: "family_cards",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "family_publications",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    relative_path = table.Column<string>(type: "text", nullable: false),
                    hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    author_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    comment = table.Column<string>(type: "text", nullable: true),
                    note = table.Column<string>(type: "text", nullable: false),
                    card_id = table.Column<Guid>(type: "uuid", nullable: true),
                    request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    version_id = table.Column<Guid>(type: "uuid", nullable: true),
                    applied_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_family_publications", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "family_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    text = table.Column<string>(type: "text", nullable: false),
                    section = table.Column<string>(type: "text", nullable: false),
                    urgency = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    project = table.Column<string>(type: "text", nullable: false),
                    state = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    author_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    assignee_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    card_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reason = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    search_text = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_family_requests", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "family_request_files",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    file_name = table.Column<string>(type: "text", nullable: false),
                    content_type = table.Column<string>(type: "text", nullable: false),
                    size = table.Column<long>(type: "bigint", nullable: false),
                    uploaded_by = table.Column<Guid>(type: "uuid", nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_family_request_files", x => x.id);
                    table.ForeignKey(
                        name: "fk_family_request_files_family_requests_request_id",
                        column: x => x.request_id,
                        principalTable: "family_requests",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "family_request_members",
                columns: table => new
                {
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    joined_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_family_request_members", x => new { x.request_id, x.user_id });
                    table.ForeignKey(
                        name: "fk_family_request_members_family_requests_request_id",
                        column: x => x.request_id,
                        principalTable: "family_requests",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "family_request_messages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    author_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    text = table.Column<string>(type: "text", nullable: false),
                    is_event = table.Column<bool>(type: "boolean", nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_family_request_messages", x => x.id);
                    table.ForeignKey(
                        name: "fk_family_request_messages_family_requests_request_id",
                        column: x => x.request_id,
                        principalTable: "family_requests",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_family_loads_card_id_at",
                table: "family_loads",
                columns: new[] { "card_id", "at" });

            migrationBuilder.CreateIndex(
                name: "ix_family_publications_hash_applied_at",
                table: "family_publications",
                columns: new[] { "hash", "applied_at" });

            migrationBuilder.CreateIndex(
                name: "ix_family_request_files_request_id",
                table: "family_request_files",
                column: "request_id");

            migrationBuilder.CreateIndex(
                name: "ix_family_request_members_user_id",
                table: "family_request_members",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_family_request_messages_request_id_at",
                table: "family_request_messages",
                columns: new[] { "request_id", "at" });

            migrationBuilder.CreateIndex(
                name: "ix_family_requests_number",
                table: "family_requests",
                column: "number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_family_requests_state_updated_at",
                table: "family_requests",
                columns: new[] { "state", "updated_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "family_loads");

            migrationBuilder.DropTable(
                name: "family_publications");

            migrationBuilder.DropTable(
                name: "family_request_files");

            migrationBuilder.DropTable(
                name: "family_request_members");

            migrationBuilder.DropTable(
                name: "family_request_messages");

            migrationBuilder.DropTable(
                name: "family_requests");

            migrationBuilder.DropColumn(
                name: "catalog_hash",
                table: "family_issues");

            migrationBuilder.DropColumn(
                name: "dismissed",
                table: "family_issues");

            migrationBuilder.DropColumn(
                name: "resolution",
                table: "family_issues");

            migrationBuilder.DropColumn(
                name: "revit_year",
                table: "family_issues");

            migrationBuilder.DropColumn(
                name: "size",
                table: "family_issues");

            migrationBuilder.DropColumn(
                name: "last_loaded_at",
                table: "family_cards");

            migrationBuilder.DropColumn(
                name: "load_count",
                table: "family_cards");
        }
    }
}
