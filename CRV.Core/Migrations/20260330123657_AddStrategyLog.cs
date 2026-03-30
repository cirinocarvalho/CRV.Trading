using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CRV.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddStrategyLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "StrategyLogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    BrokerStrategyId = table.Column<string>(type: "TEXT", nullable: false),
                    SetupId = table.Column<string>(type: "TEXT", nullable: false),
                    Ticker = table.Column<string>(type: "TEXT", nullable: false),
                    DirectionStr = table.Column<string>(type: "TEXT", nullable: false),
                    TotalContracts = table.Column<int>(type: "INTEGER", nullable: false),
                    PartialContracts = table.Column<int>(type: "INTEGER", nullable: false),
                    UseBe = table.Column<bool>(type: "INTEGER", nullable: false),
                    PointValue = table.Column<decimal>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    IsCompleted = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StrategyLogs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StrategyLogs_BrokerStrategyId",
                table: "StrategyLogs",
                column: "BrokerStrategyId");

            migrationBuilder.CreateIndex(
                name: "IX_StrategyLogs_CreatedAt",
                table: "StrategyLogs",
                column: "CreatedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StrategyLogs");
        }
    }
}
