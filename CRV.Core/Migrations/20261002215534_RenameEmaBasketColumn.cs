using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CRV.Core.Migrations
{
    /// <summary>
    /// The EMA basket column is named for the basket, not for one strategy. Content is kept as is;
    /// stored EMA21 entries (type 4) stay in it and are reported as disabled at engine start.
    /// </summary>
    public partial class RenameEmaBasketColumn : Migration
    {
        /// <summary>Column name while the basket held only EMA21 entries.</summary>
        public const string OldName = "Ema21BasketJson";
        public const string NewName = "EmaBasketJson";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(name: OldName, table: "Configs", newName: NewName);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(name: NewName, table: "Configs", newName: OldName);
        }
    }
}
