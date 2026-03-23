using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CRV.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddPerSetupInstrumentOverride : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "PointValueA",
                table: "Configs",
                type: "TEXT",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "PointValueB",
                table: "Configs",
                type: "TEXT",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "PointValueC",
                table: "Configs",
                type: "TEXT",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "PointValueD",
                table: "Configs",
                type: "TEXT",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "TickSizeA",
                table: "Configs",
                type: "TEXT",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "TickSizeB",
                table: "Configs",
                type: "TEXT",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "TickSizeC",
                table: "Configs",
                type: "TEXT",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "TickSizeD",
                table: "Configs",
                type: "TEXT",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "TickerA",
                table: "Configs",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "TickerB",
                table: "Configs",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "TickerC",
                table: "Configs",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "TickerD",
                table: "Configs",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "UseCustomTickerA",
                table: "Configs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "UseCustomTickerB",
                table: "Configs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "UseCustomTickerC",
                table: "Configs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "UseCustomTickerD",
                table: "Configs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PointValueA",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "PointValueB",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "PointValueC",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "PointValueD",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "TickSizeA",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "TickSizeB",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "TickSizeC",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "TickSizeD",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "TickerA",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "TickerB",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "TickerC",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "TickerD",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "UseCustomTickerA",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "UseCustomTickerB",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "UseCustomTickerC",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "UseCustomTickerD",
                table: "Configs");
        }
    }
}
