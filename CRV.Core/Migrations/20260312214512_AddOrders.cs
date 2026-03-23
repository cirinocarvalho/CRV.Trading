using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CRV.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddOrders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Orders",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OrderId = table.Column<string>(type: "TEXT", nullable: false),
                    Broker = table.Column<string>(type: "TEXT", nullable: false),
                    Symbol = table.Column<string>(type: "TEXT", nullable: false),
                    Action = table.Column<string>(type: "TEXT", nullable: false),
                    Quantity = table.Column<int>(type: "INTEGER", nullable: false),
                    LimitPrice = table.Column<decimal>(type: "TEXT", nullable: true),
                    StopPrice = table.Column<decimal>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    FillPrice = table.Column<decimal>(type: "TEXT", nullable: true),
                    PlacedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    FilledAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    OcoGroupId = table.Column<string>(type: "TEXT", nullable: true),
                    SessionId = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Orders", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Orders_Broker",
                table: "Orders",
                column: "Broker");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_OrderId",
                table: "Orders",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_PlacedAt",
                table: "Orders",
                column: "PlacedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Orders");
        }
    }
}
