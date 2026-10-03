using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CRV.Core.Migrations
{
    /// <summary>
    /// Restart recovery re-registered a surviving strategy and wrote a second StrategyLog row for
    /// it; completion then marked only one of the pair. With recovery looking back seven days,
    /// those leftover rows would be recovered again as duplicate trades. A strategy id that
    /// already has a completed row is finished, so its uncompleted rows are completed. A lone
    /// uncompleted row is a genuinely open position and is not touched.
    /// </summary>
    public partial class CompleteDuplicateStrategyLogs : Migration
    {
        public const string Sql = """
            UPDATE "StrategyLogs"
               SET "IsCompleted" = 1
             WHERE "IsCompleted" = 0
               AND "BrokerStrategyId" IN (SELECT "BrokerStrategyId" FROM "StrategyLogs" WHERE "IsCompleted" = 1);
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(Sql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // One-way: nothing distinguishes a completed duplicate afterwards.
        }
    }
}
