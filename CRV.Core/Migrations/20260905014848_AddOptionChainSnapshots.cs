using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CRV.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddOptionChainSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OptionChainSnapshots",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TradeDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Underlying = table.Column<string>(type: "TEXT", nullable: false),
                    UnderlyingPrice = table.Column<decimal>(type: "TEXT", nullable: false),
                    Expiration = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    DaysToExpiration = table.Column<int>(type: "INTEGER", nullable: false),
                    AtmStrike = table.Column<decimal>(type: "TEXT", nullable: false),
                    AtmImpliedVol = table.Column<decimal>(type: "TEXT", nullable: false),
                    ExpectedMove = table.Column<decimal>(type: "TEXT", nullable: true),
                    CapturedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OptionChainSnapshots", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OptionChainSnapshots_TradeDate",
                table: "OptionChainSnapshots",
                column: "TradeDate");

            migrationBuilder.CreateIndex(
                name: "IX_OptionChainSnapshots_Underlying_Expiration_TradeDate",
                table: "OptionChainSnapshots",
                columns: new[] { "Underlying", "Expiration", "TradeDate" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OptionChainSnapshots");
        }
    }
}
