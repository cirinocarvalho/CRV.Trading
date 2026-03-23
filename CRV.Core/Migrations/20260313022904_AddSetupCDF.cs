using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CRV.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddSetupCDF : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DriveBullBearRatio",
                table: "Configs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "DriveMaxPullback",
                table: "Configs",
                type: "TEXT",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "DriveRangeAtrMult",
                table: "Configs",
                type: "TEXT",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<bool>(
                name: "EnableC",
                table: "Configs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "EnableD",
                table: "Configs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "EnableF",
                table: "Configs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<decimal>(
                name: "ShallowPullbackMax",
                table: "Configs",
                type: "TEXT",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<int>(
                name: "SweepConfirmBars",
                table: "Configs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "SweepEqualTolerance",
                table: "Configs",
                type: "TEXT",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "SweepMinBodyReject",
                table: "Configs",
                type: "TEXT",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "SweepMinPenetration",
                table: "Configs",
                type: "TEXT",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<int>(
                name: "TrendDayThreshold",
                table: "Configs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "VwapDevPeriod",
                table: "Configs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DriveBullBearRatio",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "DriveMaxPullback",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "DriveRangeAtrMult",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "EnableC",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "EnableD",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "EnableF",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "ShallowPullbackMax",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "SweepConfirmBars",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "SweepEqualTolerance",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "SweepMinBodyReject",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "SweepMinPenetration",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "TrendDayThreshold",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "VwapDevPeriod",
                table: "Configs");
        }
    }
}
