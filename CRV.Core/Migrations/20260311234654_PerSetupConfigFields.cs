using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CRV.Core.Migrations
{
    /// <inheritdoc />
    public partial class PerSetupConfigFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "CloseAtRthCloseA",
                table: "Configs",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "CloseAtRthCloseB",
                table: "Configs",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "ContractsA",
                table: "Configs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 2);

            migrationBuilder.AddColumn<int>(
                name: "ContractsB",
                table: "Configs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 2);

            migrationBuilder.AddColumn<int>(
                name: "CutoffHourA",
                table: "Configs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 14);

            migrationBuilder.AddColumn<int>(
                name: "CutoffHourB",
                table: "Configs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 14);

            migrationBuilder.AddColumn<int>(
                name: "CutoffMinuteA",
                table: "Configs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 30);

            migrationBuilder.AddColumn<int>(
                name: "CutoffMinuteB",
                table: "Configs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 30);

            migrationBuilder.AddColumn<decimal>(
                name: "HiVolMultA",
                table: "Configs",
                type: "TEXT",
                nullable: false,
                defaultValue: 1.0m);

            migrationBuilder.AddColumn<decimal>(
                name: "HiVolMultB",
                table: "Configs",
                type: "TEXT",
                nullable: false,
                defaultValue: 1.0m);

            migrationBuilder.AddColumn<int>(
                name: "MaxContractsA",
                table: "Configs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 2);

            migrationBuilder.AddColumn<int>(
                name: "MaxContractsB",
                table: "Configs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 2);

            migrationBuilder.AddColumn<bool>(
                name: "UseOrbCloseA",
                table: "Configs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "UseOrbCloseB",
                table: "Configs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "UseVwapA",
                table: "Configs",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "UseVwapB",
                table: "Configs",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            // Copy existing global values into new per-setup columns
            migrationBuilder.Sql(@"
                UPDATE Configs SET
                    ContractsA = Contracts, ContractsB = Contracts,
                    CutoffHourA = CutoffHour, CutoffHourB = CutoffHour,
                    CutoffMinuteA = CutoffMinute, CutoffMinuteB = CutoffMinute,
                    UseVwapA = UseVwap, UseVwapB = UseVwap,
                    UseOrbCloseA = UseOrbClose, UseOrbCloseB = UseOrbClose,
                    CloseAtRthCloseA = CloseAtRthClose, CloseAtRthCloseB = CloseAtRthClose,
                    HiVolMultA = HiVolMult, HiVolMultB = HiVolMult,
                    MaxContractsA = MaxContracts, MaxContractsB = MaxContracts;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CloseAtRthCloseA",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "CloseAtRthCloseB",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "ContractsA",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "ContractsB",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "CutoffHourA",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "CutoffHourB",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "CutoffMinuteA",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "CutoffMinuteB",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "HiVolMultA",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "HiVolMultB",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "MaxContractsA",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "MaxContractsB",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "UseOrbCloseA",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "UseOrbCloseB",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "UseVwapA",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "UseVwapB",
                table: "Configs");
        }
    }
}
