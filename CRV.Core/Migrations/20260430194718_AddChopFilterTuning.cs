using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CRV.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddChopFilterTuning : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "ChopCompressionRatio",
                table: "Configs",
                type: "TEXT",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "ChopFlatSlopeThresholdPct",
                table: "Configs",
                type: "TEXT",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "ChopMinDriveRatio",
                table: "Configs",
                type: "TEXT",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "ChopMinVolumeRatio",
                table: "Configs",
                type: "TEXT",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<int>(
                name: "ChopMinVotes",
                table: "Configs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "ChopUseFlatVwap",
                table: "Configs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "ChopUseLowVolume",
                table: "Configs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "ChopUseRangeCompression",
                table: "Configs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "ChopUseWeakDrive",
                table: "Configs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ChopCompressionRatio",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "ChopFlatSlopeThresholdPct",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "ChopMinDriveRatio",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "ChopMinVolumeRatio",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "ChopMinVotes",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "ChopUseFlatVwap",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "ChopUseLowVolume",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "ChopUseRangeCompression",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "ChopUseWeakDrive",
                table: "Configs");
        }
    }
}
