using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CRV.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddOrderTypeCDF : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OrderTypeC",
                table: "Configs",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "OrderTypeD",
                table: "Configs",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "OrderTypeF",
                table: "Configs",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OrderTypeC",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "OrderTypeD",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "OrderTypeF",
                table: "Configs");
        }
    }
}
