using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ToadFlow.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class LoiProfileGroups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_loi_profiles_name",
                table: "loi_profiles");

            migrationBuilder.AddColumn<string>(
                name: "group",
                table: "loi_profiles",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "ix_loi_profiles_group_name",
                table: "loi_profiles",
                columns: new[] { "group", "name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_loi_profiles_group_name",
                table: "loi_profiles");

            migrationBuilder.DropColumn(
                name: "group",
                table: "loi_profiles");

            migrationBuilder.CreateIndex(
                name: "ix_loi_profiles_name",
                table: "loi_profiles",
                column: "name",
                unique: true);
        }
    }
}
