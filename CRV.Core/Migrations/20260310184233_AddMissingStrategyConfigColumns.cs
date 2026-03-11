using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CRV.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddMissingStrategyConfigColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "EntryTickOffsetA",
                table: "Configs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "EntryTickOffsetB",
                table: "Configs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ExecAccountId",
                table: "Configs",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ExecBroker",
                table: "Configs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SessionStartHour",
                table: "Configs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 18);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EntryTickOffsetA",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "EntryTickOffsetB",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "ExecAccountId",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "ExecBroker",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "SessionStartHour",
                table: "Configs");
        }
    }
}
