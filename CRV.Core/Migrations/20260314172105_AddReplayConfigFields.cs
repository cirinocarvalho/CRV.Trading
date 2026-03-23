using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CRV.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddReplayConfigFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ReplayBalance",
                table: "Configs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReplayDate",
                table: "Configs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReplaySpeed",
                table: "Configs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "SaveReplayTrades",
                table: "Configs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReplayBalance",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "ReplayDate",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "ReplaySpeed",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "SaveReplayTrades",
                table: "Configs");
        }
    }
}
