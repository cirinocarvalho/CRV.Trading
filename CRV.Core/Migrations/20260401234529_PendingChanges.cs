using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CRV.Core.Migrations
{
    /// <inheritdoc />
    public partial class PendingChanges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AutoTrailActivated",
                table: "GroupOrders",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<decimal>(
                name: "AutoTrailFreq",
                table: "GroupOrders",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "AutoTrailHighWater",
                table: "GroupOrders",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "AutoTrailStopLoss",
                table: "GroupOrders",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "AutoTrailTrigger",
                table: "GroupOrders",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AutoTrailActivated",
                table: "GroupOrders");

            migrationBuilder.DropColumn(
                name: "AutoTrailFreq",
                table: "GroupOrders");

            migrationBuilder.DropColumn(
                name: "AutoTrailHighWater",
                table: "GroupOrders");

            migrationBuilder.DropColumn(
                name: "AutoTrailStopLoss",
                table: "GroupOrders");

            migrationBuilder.DropColumn(
                name: "AutoTrailTrigger",
                table: "GroupOrders");
        }
    }
}
