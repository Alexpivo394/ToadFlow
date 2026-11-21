using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ToadFlow.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class RequestsPro : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // «Срочно» прежних заявок становится «блокирует выдачу»; вид у всех прежних — «новое семейство».
            migrationBuilder.AddColumn<string>(
                name: "kind",
                table: "family_requests",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "New");

            migrationBuilder.AddColumn<bool>(
                name: "blocks_issue",
                table: "family_requests",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.Sql("UPDATE family_requests SET blocks_issue = (urgency = 'Urgent');");

            migrationBuilder.DropColumn(
                name: "urgency",
                table: "family_requests");

            migrationBuilder.AddColumn<int>(
                name: "decline_count",
                table: "family_requests",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateOnly>(
                name: "needed_by",
                table: "family_requests",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "overdue_notified",
                table: "family_requests",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateOnly>(
                name: "promised_by",
                table: "family_requests",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "reminder_sent",
                table: "family_requests",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "resolution",
                table: "family_requests",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "review_since",
                table: "family_requests",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "revit_year",
                table: "family_requests",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "target_card_id",
                table: "family_requests",
                type: "uuid",
                nullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "request_id",
                table: "family_request_files",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<Guid>(
                name: "message_id",
                table: "family_request_files",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "urgency",
                table: "family_requests",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Normal");

            migrationBuilder.Sql("UPDATE family_requests SET urgency = CASE WHEN blocks_issue THEN 'Urgent' ELSE 'Normal' END;");

            migrationBuilder.DropColumn(
                name: "blocks_issue",
                table: "family_requests");

            migrationBuilder.DropColumn(
                name: "decline_count",
                table: "family_requests");

            migrationBuilder.DropColumn(
                name: "needed_by",
                table: "family_requests");

            migrationBuilder.DropColumn(
                name: "overdue_notified",
                table: "family_requests");

            migrationBuilder.DropColumn(
                name: "promised_by",
                table: "family_requests");

            migrationBuilder.DropColumn(
                name: "reminder_sent",
                table: "family_requests");

            migrationBuilder.DropColumn(
                name: "resolution",
                table: "family_requests");

            migrationBuilder.DropColumn(
                name: "review_since",
                table: "family_requests");

            migrationBuilder.DropColumn(
                name: "revit_year",
                table: "family_requests");

            migrationBuilder.DropColumn(
                name: "target_card_id",
                table: "family_requests");

            migrationBuilder.DropColumn(
                name: "message_id",
                table: "family_request_files");

            migrationBuilder.DropColumn(
                name: "kind",
                table: "family_requests");

            migrationBuilder.AlterColumn<Guid>(
                name: "request_id",
                table: "family_request_files",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
