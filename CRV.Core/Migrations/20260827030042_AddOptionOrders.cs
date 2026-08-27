using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CRV.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddOptionOrders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OptionOrders",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Broker = table.Column<string>(type: "TEXT", nullable: false),
                    OrderId = table.Column<string>(type: "TEXT", nullable: true),
                    Underlying = table.Column<string>(type: "TEXT", nullable: false),
                    Structure = table.Column<string>(type: "TEXT", nullable: false),
                    Intent = table.Column<string>(type: "TEXT", nullable: false),
                    Spreads = table.Column<int>(type: "INTEGER", nullable: false),
                    OrderType = table.Column<string>(type: "TEXT", nullable: false),
                    NetPrice = table.Column<decimal>(type: "TEXT", nullable: false),
                    TotalNet = table.Column<decimal>(type: "TEXT", nullable: false),
                    MaxLoss = table.Column<decimal>(type: "TEXT", nullable: true),
                    MaxProfit = table.Column<decimal>(type: "TEXT", nullable: true),
                    Breakevens = table.Column<string>(type: "TEXT", nullable: true),
                    LegsJson = table.Column<string>(type: "TEXT", nullable: false),
                    Accepted = table.Column<bool>(type: "INTEGER", nullable: false),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    PlacedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OptionOrders", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OptionOrders_OrderId",
                table: "OptionOrders",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_OptionOrders_PlacedAt",
                table: "OptionOrders",
                column: "PlacedAt");

            migrationBuilder.CreateIndex(
                name: "IX_OptionOrders_Underlying",
                table: "OptionOrders",
                column: "Underlying");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OptionOrders");
        }
    }
}
