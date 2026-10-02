using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CRV.Core.Migrations
{
    /// <summary>
    /// Single-bracket orders used to get a Tg1 leg, so their trades were saved with Target = 0,
    /// the target price in Partial, and PartialFilled set when the target filled. A real
    /// two-target trade always has a Tg2 leg, so Target = 0 with a Partial price marks exactly
    /// those rows. P&amp;L, exit and R were right and are not touched.
    /// </summary>
    public partial class RelabelLoneTargetTrades : Migration
    {
        public const string Sql = """
            UPDATE "Trades"
               SET "Target" = "Partial", "Partial" = 0, "PartialFilled" = 0, "PartialPrice" = 0
             WHERE "Target" = 0 AND "Partial" > 0;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(Sql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // One-way: the old labels were wrong, and nothing distinguishes a relabelled row afterwards.
        }
    }
}
