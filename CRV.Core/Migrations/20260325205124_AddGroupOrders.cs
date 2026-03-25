using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CRV.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddGroupOrders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GroupOrders",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GroupOrderId = table.Column<string>(type: "TEXT", nullable: false),
                    SetupId = table.Column<string>(type: "TEXT", nullable: false),
                    Ticker = table.Column<string>(type: "TEXT", nullable: false),
                    Direction = table.Column<string>(type: "TEXT", nullable: false),
                    TotalContracts = table.Column<int>(type: "INTEGER", nullable: false),
                    PartialContracts = table.Column<int>(type: "INTEGER", nullable: false),
                    EntryPrice = table.Column<decimal>(type: "TEXT", nullable: true),
                    PointValue = table.Column<decimal>(type: "TEXT", nullable: false),
                    AccruedPartialPnl = table.Column<decimal>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    Broker = table.Column<string>(type: "TEXT", nullable: false),
                    SessionId = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupOrders", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OrderLegs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GroupOrderId = table.Column<string>(type: "TEXT", nullable: false),
                    OrderId = table.Column<string>(type: "TEXT", nullable: false),
                    LegType = table.Column<string>(type: "TEXT", nullable: false),
                    OrderType = table.Column<string>(type: "TEXT", nullable: false),
                    Action = table.Column<string>(type: "TEXT", nullable: false),
                    Quantity = table.Column<int>(type: "INTEGER", nullable: false),
                    Price = table.Column<decimal>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    FillPrice = table.Column<decimal>(type: "TEXT", nullable: true),
                    FillTime = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastModifiedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrderLegs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GroupOrders_CreatedAt",
                table: "GroupOrders",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_GroupOrders_GroupOrderId",
                table: "GroupOrders",
                column: "GroupOrderId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GroupOrders_SetupId",
                table: "GroupOrders",
                column: "SetupId");

            migrationBuilder.CreateIndex(
                name: "IX_OrderLegs_GroupOrderId",
                table: "OrderLegs",
                column: "GroupOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_OrderLegs_OrderId",
                table: "OrderLegs",
                column: "OrderId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GroupOrders");

            migrationBuilder.DropTable(
                name: "OrderLegs");
        }
    }
}
