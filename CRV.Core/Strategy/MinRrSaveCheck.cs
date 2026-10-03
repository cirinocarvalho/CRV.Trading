using System.Globalization;
using CRV.Core.Models;

namespace CRV.Core.Strategy;

/// <summary>An error blocks the save; a warning lets it through and is shown with it.</summary>
public sealed record MinRrSaveResult(string? Error, string? Warning);

/// <summary>
/// Blocks saving a strategy whose target is below MinRr × its typical stop, naming the minimum
/// in the target's own unit. A range target is measured against the stop setting (the stop is
/// exactly StopPct of the range); a dollar target against the median stop of the last 30
/// backtest trades (per contract) or of contracts × stop (whole position).
/// </summary>
public static class MinRrSaveCheck
{
    public const string NoHistory        = "The reward / risk check runs once this strategy has a backtest.";
    public const string StopMovesWithBar = "The save check can't compare a bar or VWAP stop with the range; each trade is still checked.";
    public const string AtrPerTrade      = "The save check can't size an ATR target before the market sets the ATR; each trade is still checked.";

    private static readonly MinRrSaveResult Pass = new(null, null);

    public static MinRrSaveResult Check(StrategySetupConfig c, decimal pointValue, decimal? typicalStop, decimal? typicalPosition)
    {
        if (!c.EnforceMinRr) return Pass;

        switch (c.TargetMode)
        {
            case TargetMode.RangePct:
                if (c.StopMode != "OrbPct") return new(null, StopMovesWithBar);
                decimal minPct = c.MinRr * c.StopPct * 100m;
                return c.TargetPct >= minPct ? Pass : Fail($"{Math.Ceiling(minPct):0}% of the range");

            case TargetMode.Dollars:
                bool whole = c.TargetDollarsBasis == TargetDollarsBasis.WholePosition;
                if ((whole ? typicalPosition : typicalStop) is not decimal points) return new(null, NoHistory);
                decimal minDollars = c.MinRr * points * pointValue;
                return c.TargetDollars >= minDollars
                    ? Pass
                    : Fail($"${Math.Ceiling(minDollars):N0} {(whole ? "for the whole position" : "a contract")}");

            case TargetMode.RiskMultiple:
                return c.AtrTp2Mult >= c.MinRr ? Pass : Fail($"{c.MinRr:0.##}R");

            default:
                return new(null, AtrPerTrade);
        }
    }

    private static MinRrSaveResult Fail(FormattableString minimum)
        => new($"Raise the target to at least {minimum.ToString(CultureInfo.InvariantCulture)}, or lower the minimum reward / risk.", null);
}
