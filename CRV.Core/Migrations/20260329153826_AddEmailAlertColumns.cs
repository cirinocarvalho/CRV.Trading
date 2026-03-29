using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CRV.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddEmailAlertColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "EmailBatchIntervalMinutes",
                table: "Configs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "EmailEnabled",
                table: "Configs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "EmailOnDailyLossBreached",
                table: "Configs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "EmailOnDailyLossBreachedMode",
                table: "Configs",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "EmailOnEngineStatus",
                table: "Configs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "EmailOnEngineStatusMode",
                table: "Configs",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "EmailOnEntry",
                table: "Configs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "EmailOnEntryMode",
                table: "Configs",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "EmailOnExit",
                table: "Configs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "EmailOnExitMode",
                table: "Configs",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "EmailOnOrbFormed",
                table: "Configs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "EmailOnOrbFormedMode",
                table: "Configs",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "EmailOnSessionChange",
                table: "Configs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "EmailOnSessionChangeMode",
                table: "Configs",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "EmailOnSessionEnd",
                table: "Configs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "EmailRecipients",
                table: "Configs",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EmailBatchIntervalMinutes",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "EmailEnabled",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "EmailOnDailyLossBreached",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "EmailOnDailyLossBreachedMode",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "EmailOnEngineStatus",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "EmailOnEngineStatusMode",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "EmailOnEntry",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "EmailOnEntryMode",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "EmailOnExit",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "EmailOnExitMode",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "EmailOnOrbFormed",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "EmailOnOrbFormedMode",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "EmailOnSessionChange",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "EmailOnSessionChangeMode",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "EmailOnSessionEnd",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "EmailRecipients",
                table: "Configs");
        }
    }
}
