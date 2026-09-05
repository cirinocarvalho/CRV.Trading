using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CRV.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddOptionFillInstrumentation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "FilledAt",
                table: "OptionOrders",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "FilledNetPrice",
                table: "OptionOrders",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MarketAtSubmitJson",
                table: "OptionOrders",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "MidNetPrice",
                table: "OptionOrders",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FilledAt",
                table: "OptionOrders");

            migrationBuilder.DropColumn(
                name: "FilledNetPrice",
                table: "OptionOrders");

            migrationBuilder.DropColumn(
                name: "MarketAtSubmitJson",
                table: "OptionOrders");

            migrationBuilder.DropColumn(
                name: "MidNetPrice",
                table: "OptionOrders");
        }
    }
}
