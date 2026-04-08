using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CRV.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddEma21Strategy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "AtrTouchMultEma21",
                table: "Configs",
                type: "TEXT",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "AtrTp1MultEma21",
                table: "Configs",
                type: "TEXT",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "AtrTp2MultEma21",
                table: "Configs",
                type: "TEXT",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<int>(
                name: "ContractsEma21",
                table: "Configs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "CutoffHourEma21",
                table: "Configs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "CutoffMinuteEma21",
                table: "Configs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "EnableEma21",
                table: "Configs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "MaxContractsEma21",
                table: "Configs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "MaxTradesEma21",
                table: "Configs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "MinRrEma21",
                table: "Configs",
                type: "TEXT",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "MinSlopePctEma21",
                table: "Configs",
                type: "TEXT",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<int>(
                name: "OpenTicksToEmaEma21",
                table: "Configs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "OrderTypeEma21",
                table: "Configs",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "PartialCtsEma21",
                table: "Configs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "PointValueEma21",
                table: "Configs",
                type: "TEXT",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<int>(
                name: "SlopeLenEma21",
                table: "Configs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "TickSizeEma21",
                table: "Configs",
                type: "TEXT",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "TickerEma21",
                table: "Configs",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "UseBeEma21",
                table: "Configs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "UseCustomTickerEma21",
                table: "Configs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "UsePartialEma21",
                table: "Configs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "UseVolumeFilterEma21",
                table: "Configs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AtrTouchMultEma21",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "AtrTp1MultEma21",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "AtrTp2MultEma21",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "ContractsEma21",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "CutoffHourEma21",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "CutoffMinuteEma21",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "EnableEma21",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "MaxContractsEma21",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "MaxTradesEma21",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "MinRrEma21",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "MinSlopePctEma21",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "OpenTicksToEmaEma21",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "OrderTypeEma21",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "PartialCtsEma21",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "PointValueEma21",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "SlopeLenEma21",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "TickSizeEma21",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "TickerEma21",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "UseBeEma21",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "UseCustomTickerEma21",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "UsePartialEma21",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "UseVolumeFilterEma21",
                table: "Configs");
        }
    }
}
