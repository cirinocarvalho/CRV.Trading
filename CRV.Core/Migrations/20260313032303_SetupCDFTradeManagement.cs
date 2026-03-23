using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CRV.Core.Migrations
{
    /// <inheritdoc />
    public partial class SetupCDFTradeManagement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "CloseAtRthCloseC",
                table: "Configs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "CloseAtRthCloseD",
                table: "Configs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "CloseAtRthCloseF",
                table: "Configs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "ContractsC",
                table: "Configs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ContractsD",
                table: "Configs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ContractsF",
                table: "Configs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "CutoffHourC",
                table: "Configs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "CutoffHourD",
                table: "Configs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "CutoffHourF",
                table: "Configs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "CutoffMinuteC",
                table: "Configs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "CutoffMinuteD",
                table: "Configs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "CutoffMinuteF",
                table: "Configs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "HiVolMultC",
                table: "Configs",
                type: "TEXT",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "HiVolMultD",
                table: "Configs",
                type: "TEXT",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "HiVolMultF",
                table: "Configs",
                type: "TEXT",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<int>(
                name: "MaxContractsC",
                table: "Configs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "MaxContractsD",
                table: "Configs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "MaxContractsF",
                table: "Configs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "MaxTradesC",
                table: "Configs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "MaxTradesD",
                table: "Configs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "MaxTradesF",
                table: "Configs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "MinRrC",
                table: "Configs",
                type: "TEXT",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "MinRrD",
                table: "Configs",
                type: "TEXT",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "MinRrF",
                table: "Configs",
                type: "TEXT",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<int>(
                name: "PartialPctC",
                table: "Configs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "PartialPctD",
                table: "Configs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "PartialPctF",
                table: "Configs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "UseBeC",
                table: "Configs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "UseBeD",
                table: "Configs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "UseBeF",
                table: "Configs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "UsePartialC",
                table: "Configs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "UsePartialD",
                table: "Configs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "UsePartialF",
                table: "Configs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CloseAtRthCloseC",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "CloseAtRthCloseD",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "CloseAtRthCloseF",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "ContractsC",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "ContractsD",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "ContractsF",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "CutoffHourC",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "CutoffHourD",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "CutoffHourF",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "CutoffMinuteC",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "CutoffMinuteD",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "CutoffMinuteF",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "HiVolMultC",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "HiVolMultD",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "HiVolMultF",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "MaxContractsC",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "MaxContractsD",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "MaxContractsF",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "MaxTradesC",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "MaxTradesD",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "MaxTradesF",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "MinRrC",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "MinRrD",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "MinRrF",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "PartialPctC",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "PartialPctD",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "PartialPctF",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "UseBeC",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "UseBeD",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "UseBeF",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "UsePartialC",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "UsePartialD",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "UsePartialF",
                table: "Configs");
        }
    }
}
