using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CRV.Core.Migrations
{
    /// <inheritdoc />
    public partial class FixFalseBreakoutDefaults : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Fix defaults for False Breakout columns that were added/renamed
            // with defaultValue: 0 in the previous migration.
            migrationBuilder.Sql(@"
                UPDATE Configs SET
                    FBMaxTimeOutsideMinutesOrb = 15   WHERE FBMaxTimeOutsideMinutesOrb = 0;
            ");
            migrationBuilder.Sql(@"
                UPDATE Configs SET
                    FBMaxTimeOutsideMinutesSR  = 60   WHERE FBMaxTimeOutsideMinutesSR  = 0;
            ");
            migrationBuilder.Sql(@"
                UPDATE Configs SET
                    FBMaxPenetrationPctOrb     = '0.30' WHERE FBMaxPenetrationPctOrb = '0' OR FBMaxPenetrationPctOrb = '0.0';
            ");
            migrationBuilder.Sql(@"
                UPDATE Configs SET
                    FBMaxPenetrationPctSR      = '0.25' WHERE FBMaxPenetrationPctSR  = '0' OR FBMaxPenetrationPctSR  = '0.0';
            ");
            migrationBuilder.Sql(@"
                UPDATE Configs SET
                    FBMinRejectionBodyPct      = '0.50' WHERE FBMinRejectionBodyPct  = '0' OR FBMinRejectionBodyPct  = '0.0';
            ");
            migrationBuilder.Sql(@"
                UPDATE Configs SET
                    FBMaxTrendDayScore         = 60    WHERE FBMaxTrendDayScore       = 0;
            ");
            migrationBuilder.Sql(@"
                UPDATE Configs SET
                    NearPctC = '0.15' WHERE NearPctC = '0' OR NearPctC = '0.0';
            ");
            migrationBuilder.Sql(@"
                UPDATE Configs SET
                    NearPctD = '0.15' WHERE NearPctD = '0' OR NearPctD = '0.0';
            ");
            migrationBuilder.Sql(@"
                UPDATE Configs SET
                    StopPctC = '0.10' WHERE StopPctC = '0' OR StopPctC = '0.0';
            ");
            migrationBuilder.Sql(@"
                UPDATE Configs SET
                    StopPctD = '0.10' WHERE StopPctD = '0' OR StopPctD = '0.0';
            ");
            migrationBuilder.Sql(@"
                UPDATE Configs SET
                    TargetPctC = 100 WHERE TargetPctC = 0;
            ");
            migrationBuilder.Sql(@"
                UPDATE Configs SET
                    TargetPctD = 100 WHERE TargetPctD = 0;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
