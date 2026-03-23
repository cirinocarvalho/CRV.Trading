using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CRV.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddNearPctAAndNearPctB : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "NearPctA",
                table: "Configs",
                type: "TEXT",
                nullable: false,
                defaultValue: 0.15m);

            migrationBuilder.AddColumn<decimal>(
                name: "NearPctB",
                table: "Configs",
                type: "TEXT",
                nullable: false,
                defaultValue: 0.15m);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "NearPctA",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "NearPctB",
                table: "Configs");
        }
    }
}
