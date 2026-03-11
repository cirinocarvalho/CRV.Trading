using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CRV.Core.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BacktestRuns",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Ticker = table.Column<string>(type: "TEXT", nullable: false),
                    ConfigName = table.Column<string>(type: "TEXT", nullable: false),
                    From = table.Column<DateTime>(type: "TEXT", nullable: false),
                    To = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DataSource = table.Column<string>(type: "TEXT", nullable: false),
                    FillMode = table.Column<string>(type: "TEXT", nullable: false),
                    TotalTrades = table.Column<int>(type: "INTEGER", nullable: false),
                    NetPnl = table.Column<decimal>(type: "TEXT", nullable: false),
                    WinRate = table.Column<decimal>(type: "TEXT", nullable: false),
                    ProfitFactor = table.Column<decimal>(type: "TEXT", nullable: false),
                    MaxDrawdown = table.Column<decimal>(type: "TEXT", nullable: false),
                    RunAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ResultJson = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BacktestRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Configs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Ticker = table.Column<string>(type: "TEXT", nullable: false),
                    Exchange = table.Column<string>(type: "TEXT", nullable: false),
                    PointValue = table.Column<decimal>(type: "TEXT", nullable: false),
                    TickSize = table.Column<decimal>(type: "TEXT", nullable: false),
                    ExecutionTFMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    Timezone = table.Column<string>(type: "TEXT", nullable: false),
                    OrbStart = table.Column<TimeOnly>(type: "TEXT", nullable: false),
                    OrbEnd = table.Column<TimeOnly>(type: "TEXT", nullable: false),
                    RthStart = table.Column<TimeOnly>(type: "TEXT", nullable: false),
                    RthEnd = table.Column<TimeOnly>(type: "TEXT", nullable: false),
                    AtrFilterPct = table.Column<decimal>(type: "TEXT", nullable: false),
                    UseTimeFilter = table.Column<bool>(type: "INTEGER", nullable: false),
                    CutoffHour = table.Column<int>(type: "INTEGER", nullable: false),
                    CutoffMinute = table.Column<int>(type: "INTEGER", nullable: false),
                    UseDailyLossLimit = table.Column<bool>(type: "INTEGER", nullable: false),
                    MaxDailyLoss = table.Column<decimal>(type: "TEXT", nullable: false),
                    UseVwap = table.Column<bool>(type: "INTEGER", nullable: false),
                    UseOrbClose = table.Column<bool>(type: "INTEGER", nullable: false),
                    AllowBothSameBar = table.Column<bool>(type: "INTEGER", nullable: false),
                    Contracts = table.Column<int>(type: "INTEGER", nullable: false),
                    HiVolMult = table.Column<decimal>(type: "TEXT", nullable: false),
                    MaxContracts = table.Column<int>(type: "INTEGER", nullable: false),
                    Broker = table.Column<string>(type: "TEXT", nullable: false),
                    AccountId = table.Column<string>(type: "TEXT", nullable: false),
                    CommissionPerSide = table.Column<decimal>(type: "TEXT", nullable: false),
                    EnableA = table.Column<bool>(type: "INTEGER", nullable: false),
                    ModeA = table.Column<string>(type: "TEXT", nullable: false),
                    MaxTradesA = table.Column<int>(type: "INTEGER", nullable: false),
                    NearPct = table.Column<decimal>(type: "TEXT", nullable: false),
                    PullbackPct = table.Column<decimal>(type: "TEXT", nullable: false),
                    StopPctA = table.Column<decimal>(type: "TEXT", nullable: false),
                    TargetPctA = table.Column<int>(type: "INTEGER", nullable: false),
                    PartialPctA = table.Column<int>(type: "INTEGER", nullable: false),
                    MinRrA = table.Column<decimal>(type: "TEXT", nullable: false),
                    UsePartialA = table.Column<bool>(type: "INTEGER", nullable: false),
                    UseBeA = table.Column<bool>(type: "INTEGER", nullable: false),
                    EnableB = table.Column<bool>(type: "INTEGER", nullable: false),
                    ModeB = table.Column<string>(type: "TEXT", nullable: false),
                    MaxTradesB = table.Column<int>(type: "INTEGER", nullable: false),
                    RetestPct = table.Column<decimal>(type: "TEXT", nullable: false),
                    TargetPctB = table.Column<int>(type: "INTEGER", nullable: false),
                    PartialPctB = table.Column<int>(type: "INTEGER", nullable: false),
                    MinRrB = table.Column<decimal>(type: "TEXT", nullable: false),
                    UsePartialB = table.Column<bool>(type: "INTEGER", nullable: false),
                    UseBeB = table.Column<bool>(type: "INTEGER", nullable: false),
                    CloseAtRthClose = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Configs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Trades",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SessionId = table.Column<string>(type: "TEXT", nullable: false),
                    Source = table.Column<string>(type: "TEXT", nullable: false),
                    Setup = table.Column<string>(type: "TEXT", nullable: false),
                    Direction = table.Column<string>(type: "TEXT", nullable: false),
                    Ticker = table.Column<string>(type: "TEXT", nullable: false),
                    Contracts = table.Column<int>(type: "INTEGER", nullable: false),
                    Entry = table.Column<decimal>(type: "TEXT", nullable: false),
                    InitialStop = table.Column<decimal>(type: "TEXT", nullable: false),
                    Target = table.Column<decimal>(type: "TEXT", nullable: false),
                    Partial = table.Column<decimal>(type: "TEXT", nullable: false),
                    Exit = table.Column<decimal>(type: "TEXT", nullable: false),
                    ExitReason = table.Column<string>(type: "TEXT", nullable: false),
                    PartialFilled = table.Column<bool>(type: "INTEGER", nullable: false),
                    PartialPrice = table.Column<decimal>(type: "TEXT", nullable: false),
                    GrossPnl = table.Column<decimal>(type: "TEXT", nullable: false),
                    Commission = table.Column<decimal>(type: "TEXT", nullable: false),
                    NetPnl = table.Column<decimal>(type: "TEXT", nullable: false),
                    RMultiple = table.Column<decimal>(type: "TEXT", nullable: false),
                    EnteredAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExitedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Trades", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Trades_EnteredAt",
                table: "Trades",
                column: "EnteredAt");

            migrationBuilder.CreateIndex(
                name: "IX_Trades_ExitReason",
                table: "Trades",
                column: "ExitReason");

            migrationBuilder.CreateIndex(
                name: "IX_Trades_SessionId",
                table: "Trades",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_Trades_Source",
                table: "Trades",
                column: "Source");

            migrationBuilder.CreateIndex(
                name: "IX_Trades_Ticker",
                table: "Trades",
                column: "Ticker");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BacktestRuns");

            migrationBuilder.DropTable(
                name: "Configs");

            migrationBuilder.DropTable(
                name: "Trades");
        }
    }
}
