using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CRV.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddAllowRearmAfterBE : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AllowRearmAfterBeA",
                table: "Configs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "AllowRearmAfterBeB",
                table: "Configs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AllowRearmAfterBeA",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "AllowRearmAfterBeB",
                table: "Configs");
        }
    }
}
