using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CRV.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddMaxTradeRisk : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "MaxTradeRiskA",
                table: "Configs",
                type: "TEXT",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "MaxTradeRiskB",
                table: "Configs",
                type: "TEXT",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "MaxTradeRiskC",
                table: "Configs",
                type: "TEXT",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "MaxTradeRiskD",
                table: "Configs",
                type: "TEXT",
                nullable: false,
                defaultValue: 0m);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MaxTradeRiskA",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "MaxTradeRiskB",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "MaxTradeRiskC",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "MaxTradeRiskD",
                table: "Configs");
        }
    }
}
