using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ToadFlow.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class UsageTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "usage_command_days",
                columns: table => new
                {
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    day = table.Column<DateOnly>(type: "date", nullable: false),
                    source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    command_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    machine_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    tab = table.Column<string>(type: "text", nullable: true),
                    count = table.Column<int>(type: "integer", nullable: false),
                    failed = table.Column<int>(type: "integer", nullable: false),
                    cancelled = table.Column<int>(type: "integer", nullable: false),
                    duration_ms = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_usage_command_days", x => new { x.session_id, x.day, x.source, x.command_id });
                });

            migrationBuilder.CreateTable(
                name: "usage_machines",
                columns: table => new
                {
                    key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    cpu = table.Column<string>(type: "text", nullable: true),
                    cores = table.Column<int>(type: "integer", nullable: true),
                    ram_mb = table.Column<int>(type: "integer", nullable: true),
                    gpu = table.Column<string>(type: "text", nullable: true),
                    gpu_memory_mb = table.Column<int>(type: "integer", nullable: true),
                    disk_kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    disk_total_gb = table.Column<int>(type: "integer", nullable: true),
                    disk_free_gb = table.Column<int>(type: "integer", nullable: true),
                    os = table.Column<string>(type: "text", nullable: true),
                    revit_years = table.Column<int[]>(type: "integer[]", nullable: false, defaultValueSql: "'{}'::integer[]"),
                    toad_tools = table.Column<string>(type: "text", nullable: true),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_user_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_usage_machines", x => x.key);
                });

            migrationBuilder.CreateTable(
                name: "usage_operations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    machine_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    duration_ms = table.Column<long>(type: "bigint", nullable: false),
                    outcome = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    model_name = table.Column<string>(type: "text", nullable: false),
                    model_path = table.Column<string>(type: "text", nullable: false),
                    model_key = table.Column<string>(type: "text", nullable: false),
                    storage = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    server_host = table.Column<string>(type: "text", nullable: true),
                    host_model_key = table.Column<string>(type: "text", nullable: true),
                    file_size_bytes = table.Column<long>(type: "bigint", nullable: true),
                    free_memory_mb = table.Column<int>(type: "integer", nullable: true),
                    revit_year = table.Column<int>(type: "integer", nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_usage_operations", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "usage_plugin_days",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    day = table.Column<DateOnly>(type: "date", nullable: false),
                    area = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    count = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_usage_plugin_days", x => new { x.user_id, x.day, x.area });
                });

            migrationBuilder.CreateTable(
                name: "usage_session_days",
                columns: table => new
                {
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    day = table.Column<DateOnly>(type: "date", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    machine_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    active_seconds = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_usage_session_days", x => new { x.session_id, x.day });
                });

            migrationBuilder.CreateTable(
                name: "usage_sessions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    machine_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    windows_user = table.Column<string>(type: "text", nullable: true),
                    revit_year = table.Column<int>(type: "integer", nullable: false),
                    revit_build = table.Column<string>(type: "text", nullable: true),
                    toad_tools = table.Column<string>(type: "text", nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    active_seconds = table.Column<int>(type: "integer", nullable: false),
                    end = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    peak_memory_mb = table.Column<int>(type: "integer", nullable: true),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_usage_sessions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "usage_web_days",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    day = table.Column<DateOnly>(type: "date", nullable: false),
                    space = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    page = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    count = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_usage_web_days", x => new { x.user_id, x.day, x.space, x.page });
                });

            migrationBuilder.CreateIndex(
                name: "ix_usage_command_days_day",
                table: "usage_command_days",
                column: "day");

            migrationBuilder.CreateIndex(
                name: "ix_usage_command_days_user_id_day",
                table: "usage_command_days",
                columns: new[] { "user_id", "day" });

            migrationBuilder.CreateIndex(
                name: "ix_usage_machines_last_seen_at",
                table: "usage_machines",
                column: "last_seen_at");

            migrationBuilder.CreateIndex(
                name: "ix_usage_operations_machine_key_started_at",
                table: "usage_operations",
                columns: new[] { "machine_key", "started_at" });

            migrationBuilder.CreateIndex(
                name: "ix_usage_operations_model_key_started_at",
                table: "usage_operations",
                columns: new[] { "model_key", "started_at" });

            migrationBuilder.CreateIndex(
                name: "ix_usage_operations_started_at",
                table: "usage_operations",
                column: "started_at");

            migrationBuilder.CreateIndex(
                name: "ix_usage_operations_user_id_started_at",
                table: "usage_operations",
                columns: new[] { "user_id", "started_at" });

            migrationBuilder.CreateIndex(
                name: "ix_usage_plugin_days_day",
                table: "usage_plugin_days",
                column: "day");

            migrationBuilder.CreateIndex(
                name: "ix_usage_session_days_day",
                table: "usage_session_days",
                column: "day");

            migrationBuilder.CreateIndex(
                name: "ix_usage_sessions_machine_key_started_at",
                table: "usage_sessions",
                columns: new[] { "machine_key", "started_at" });

            migrationBuilder.CreateIndex(
                name: "ix_usage_sessions_started_at",
                table: "usage_sessions",
                column: "started_at");

            migrationBuilder.CreateIndex(
                name: "ix_usage_sessions_user_id_started_at",
                table: "usage_sessions",
                columns: new[] { "user_id", "started_at" });

            migrationBuilder.CreateIndex(
                name: "ix_usage_web_days_day",
                table: "usage_web_days",
                column: "day");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "usage_command_days");

            migrationBuilder.DropTable(
                name: "usage_machines");

            migrationBuilder.DropTable(
                name: "usage_operations");

            migrationBuilder.DropTable(
                name: "usage_plugin_days");

            migrationBuilder.DropTable(
                name: "usage_session_days");

            migrationBuilder.DropTable(
                name: "usage_sessions");

            migrationBuilder.DropTable(
                name: "usage_web_days");
        }
    }
}
