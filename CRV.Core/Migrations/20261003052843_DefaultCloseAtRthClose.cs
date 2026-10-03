using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CRV.Core.Migrations
{
    /// <summary>
    /// Sets CloseAtRthClose to true on every basket entry (ORB and EMA baskets) and on legacy
    /// setups A–D, so no stored setup starts holding positions past the session until someone
    /// switches it off. Basket JSON is edited in SQL with json_set because a migration cannot
    /// run C# per row; every basket writer serializes PascalCase, so "$.Config.CloseAtRthClose"
    /// is the key BasketCodec reads.
    /// </summary>
    public partial class DefaultCloseAtRthClose : Migration
    {
        /// <summary>The EMA basket column when this migration runs.</summary>
        public const string EmaBasketColumn = "EmaBasketJson";

        /// <summary>Sets Config.CloseAtRthClose on every entry of the basket JSON in <paramref name="column"/>.
        /// Empty, NULL and non-array values are left alone.</summary>
        public static string BasketSql(string column) => $"""
            UPDATE "Configs"
               SET "{column}" = (
                   SELECT json_group_array(json_set(e.value, '$.Config.CloseAtRthClose', json('true')))
                     FROM json_each("Configs"."{column}") AS e)
             WHERE json_valid("{column}") AND json_type("{column}") = 'array';
            """;

        public const string LegacySql = """
            UPDATE "Configs"
               SET "CloseAtRthCloseA" = 1, "CloseAtRthCloseB" = 1, "CloseAtRthCloseC" = 1, "CloseAtRthCloseD" = 1;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(BasketSql("BasketJson"));
            migrationBuilder.Sql(BasketSql(EmaBasketColumn));
            migrationBuilder.Sql(LegacySql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // One-way: nothing records which rows held false before.
        }
    }
}
