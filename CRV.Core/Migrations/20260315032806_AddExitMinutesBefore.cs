using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CRV.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddExitMinutesBefore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ExitMinutesBefore",
                table: "Configs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExitMinutesBefore",
                table: "Configs");
        }
    }
}
