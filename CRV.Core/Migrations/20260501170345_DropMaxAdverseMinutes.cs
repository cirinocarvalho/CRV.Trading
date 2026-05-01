using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CRV.Core.Migrations
{
    /// <inheritdoc />
    public partial class DropMaxAdverseMinutes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MaxAdverseMinutesA",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "MaxAdverseMinutesB",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "MaxAdverseMinutesC",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "MaxAdverseMinutesD",
                table: "Configs");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MaxAdverseMinutesA",
                table: "Configs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "MaxAdverseMinutesB",
                table: "Configs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "MaxAdverseMinutesC",
                table: "Configs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "MaxAdverseMinutesD",
                table: "Configs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }
    }
}
