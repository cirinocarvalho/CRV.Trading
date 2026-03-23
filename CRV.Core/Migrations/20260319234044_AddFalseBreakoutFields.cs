using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CRV.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddFalseBreakoutFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CloseAtRthCloseF",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "ContractsF",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "CutoffHourF",
                table: "Configs");

            migrationBuilder.RenameColumn(
                name: "UsePartialF",
                table: "Configs",
                newName: "TargetPctD");

            migrationBuilder.RenameColumn(
                name: "UseBeF",
                table: "Configs",
                newName: "TargetPctC");

            migrationBuilder.RenameColumn(
                name: "PartialPctF",
                table: "Configs",
                newName: "FBMaxTrendDayScore");

            migrationBuilder.RenameColumn(
                name: "PartialCtsF",
                table: "Configs",
                newName: "FBMaxTimeOutsideMinutesSR");

            migrationBuilder.RenameColumn(
                name: "OrderTypeF",
                table: "Configs",
                newName: "StopPctD");

            migrationBuilder.RenameColumn(
                name: "MinRrF",
                table: "Configs",
                newName: "StopPctC");

            migrationBuilder.RenameColumn(
                name: "MaxTradesF",
                table: "Configs",
                newName: "FBMaxTimeOutsideMinutesOrb");

            migrationBuilder.RenameColumn(
                name: "MaxContractsF",
                table: "Configs",
                newName: "EntryTickOffsetD");

            migrationBuilder.RenameColumn(
                name: "MaxAdverseMinutesF",
                table: "Configs",
                newName: "EntryTickOffsetC");

            migrationBuilder.RenameColumn(
                name: "HiVolMultF",
                table: "Configs",
                newName: "NearPctD");

            migrationBuilder.RenameColumn(
                name: "EnableF",
                table: "Configs",
                newName: "AllowRearmAfterBeD");

            migrationBuilder.RenameColumn(
                name: "CutoffMinuteF",
                table: "Configs",
                newName: "AllowRearmAfterBeC");

            migrationBuilder.AddColumn<decimal>(
                name: "FBMaxPenetrationPctOrb",
                table: "Configs",
                type: "TEXT",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "FBMaxPenetrationPctSR",
                table: "Configs",
                type: "TEXT",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "FBMinRejectionBodyPct",
                table: "Configs",
                type: "TEXT",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "NearPctC",
                table: "Configs",
                type: "TEXT",
                nullable: false,
                defaultValue: 0m);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FBMaxPenetrationPctOrb",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "FBMaxPenetrationPctSR",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "FBMinRejectionBodyPct",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "NearPctC",
                table: "Configs");

            migrationBuilder.RenameColumn(
                name: "TargetPctD",
                table: "Configs",
                newName: "UsePartialF");

            migrationBuilder.RenameColumn(
                name: "TargetPctC",
                table: "Configs",
                newName: "UseBeF");

            migrationBuilder.RenameColumn(
                name: "StopPctD",
                table: "Configs",
                newName: "OrderTypeF");

            migrationBuilder.RenameColumn(
                name: "StopPctC",
                table: "Configs",
                newName: "MinRrF");

            migrationBuilder.RenameColumn(
                name: "NearPctD",
                table: "Configs",
                newName: "HiVolMultF");

            migrationBuilder.RenameColumn(
                name: "FBMaxTrendDayScore",
                table: "Configs",
                newName: "PartialPctF");

            migrationBuilder.RenameColumn(
                name: "FBMaxTimeOutsideMinutesSR",
                table: "Configs",
                newName: "PartialCtsF");

            migrationBuilder.RenameColumn(
                name: "FBMaxTimeOutsideMinutesOrb",
                table: "Configs",
                newName: "MaxTradesF");

            migrationBuilder.RenameColumn(
                name: "EntryTickOffsetD",
                table: "Configs",
                newName: "MaxContractsF");

            migrationBuilder.RenameColumn(
                name: "EntryTickOffsetC",
                table: "Configs",
                newName: "MaxAdverseMinutesF");

            migrationBuilder.RenameColumn(
                name: "AllowRearmAfterBeD",
                table: "Configs",
                newName: "EnableF");

            migrationBuilder.RenameColumn(
                name: "AllowRearmAfterBeC",
                table: "Configs",
                newName: "CutoffMinuteF");

            migrationBuilder.AddColumn<bool>(
                name: "CloseAtRthCloseF",
                table: "Configs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "ContractsF",
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
        }
    }
}
