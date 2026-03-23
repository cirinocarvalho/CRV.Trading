using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CRV.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddPartialCtsPerSetup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PartialCtsA",
                table: "Configs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "PartialCtsB",
                table: "Configs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "PartialCtsC",
                table: "Configs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "PartialCtsD",
                table: "Configs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "PartialCtsF",
                table: "Configs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PartialCtsA",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "PartialCtsB",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "PartialCtsC",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "PartialCtsD",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "PartialCtsF",
                table: "Configs");
        }
    }
}
