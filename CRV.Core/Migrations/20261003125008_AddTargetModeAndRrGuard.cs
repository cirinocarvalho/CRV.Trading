using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CRV.Core.Migrations
{
    /// <summary>
    /// Writes the target and reward / risk guard settings into every stored basket entry:
    /// TargetMode = RangePct (0), TargetDollars = 0, TargetDollarsBasis = PerContract (0),
    /// EnforceMinRr = true, MinRrAction = Skip (0), the behaviour every strategy had before
    /// these settings existed. json_insert adds only keys that are absent and only inside a
    /// Config object; a basket that isn't a JSON array of objects is left as it is. The legacy
    /// A–D setups have no columns for these settings and take the same values from the
    /// StrategySetupConfig defaults.
    /// </summary>
    public partial class AddTargetModeAndRrGuard : Migration
    {
        public const string Sql = """
            UPDATE "Configs"
               SET "BasketJson" = (
                   SELECT json_group_array(json(
                            CASE WHEN json_type(e.value, '$.Config') = 'object'
                                 THEN json_insert(e.value,
                                        '$.Config.TargetMode', 0,
                                        '$.Config.TargetDollars', 0,
                                        '$.Config.TargetDollarsBasis', 0,
                                        '$.Config.EnforceMinRr', json('true'),
                                        '$.Config.MinRrAction', 0)
                                 ELSE e.value END))
                     FROM (SELECT value FROM json_each("Configs"."BasketJson") ORDER BY key) AS e)
             WHERE json_valid("BasketJson")
               AND json_type("BasketJson") = 'array'
               AND json_array_length("BasketJson") > 0
               AND NOT EXISTS (SELECT 1 FROM json_each("Configs"."BasketJson") WHERE type <> 'object');
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(Sql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The values written are the defaults the code reads for an absent key, so leaving them is harmless.
        }
    }
}
