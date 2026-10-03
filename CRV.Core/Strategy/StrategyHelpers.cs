using CRV.Core.Models;

namespace CRV.Core.Strategy;

/// <summary>
/// What a strategy knows when it sets its target: the fill, the final stop, the sized
/// contract count and the target settings. <see cref="RangeOrAtr"/> is the range for
/// RangePct and the ATR for Atr.
/// </summary>
public record LevelRequest(
    decimal Entry, bool IsLong, decimal Stop, int Contracts,
    TargetMode Mode, decimal RangeOrAtr, decimal TargetPct,
    decimal TargetDollars, TargetDollarsBasis Basis, decimal PointValue,
    decimal PartialPct, decimal TickSize,
    decimal Tp1Mult = 0, decimal Tp2Mult = 0)
{
    public static LevelRequest From(StrategySetupConfig cfg, decimal entry, bool isLong, decimal stop,
        int contracts, decimal rangeOrAtr) => new(
        entry, isLong, stop, contracts, cfg.TargetMode, rangeOrAtr, cfg.TargetPct,
        cfg.TargetDollars, cfg.TargetDollarsBasis, cfg.PointValue, cfg.PartialPct, cfg.TickSize,
        cfg.AtrTp1Mult, cfg.AtrTp2Mult);
}

/// <summary>Port of Pine f_calcLevels(), extended to every target mode.</summary>
public static class LevelCalculator
{
    /// <summary>
    /// Rounds <paramref name="price"/> to the nearest valid tick.
    /// tickSize = 0 means no rounding (used by existing tests without a tick parameter).
    /// </summary>
    public static decimal RoundToTick(decimal price, decimal tickSize)
        => tickSize > 0 ? Math.Round(price / tickSize, MidpointRounding.AwayFromZero) * tickSize : price;

    /// <summary>The OrbPct stop: <paramref name="stopPct"/> of the range from <paramref name="entry"/>, on the tick.</summary>
    public static decimal RangeStop(decimal entry, bool isLong, decimal range, decimal stopPct, decimal tickSize = 0m)
        => RoundToTick(isLong ? entry - range * stopPct : entry + range * stopPct, tickSize);

    /// <summary>
    /// Target and partial on the tick, measured from <see cref="LevelRequest.Entry"/>, and the
    /// reward / risk they give against <see cref="LevelRequest.Stop"/> (0 when there is no risk).
    /// </summary>
    public static (decimal Target, decimal Partial, decimal Rr) Targets(LevelRequest r)
    {
        decimal risk        = Math.Abs(r.Entry - r.Stop);
        decimal dist        = TargetDistance(r, risk);
        decimal partialDist = PartialDistance(r, risk, dist);

        decimal target  = RoundToTick(r.IsLong ? r.Entry + dist : r.Entry - dist, r.TickSize);
        decimal partial = PartialPrice(r, partialDist);

        decimal reward = Math.Abs(target - r.Entry);
        return (target, partial, risk > 0 ? reward / risk : 0m);
    }

    /// <summary>
    /// The target moved out to <paramref name="minRr"/> × the risk, rounded away from the entry so
    /// the trade is never below the minimum, and the partial at PartialPct of the new distance.
    /// </summary>
    public static (decimal Target, decimal Partial) RaiseToMinRr(LevelRequest r, decimal minRr)
    {
        decimal dist   = Math.Abs(r.Entry - r.Stop) * minRr;
        decimal target = r.IsLong ? CeilToTick(r.Entry + dist, r.TickSize) : FloorToTick(r.Entry - dist, r.TickSize);

        return (target, PartialPrice(r, Math.Abs(target - r.Entry) * (r.PartialPct / 100m)));
    }

    /// <summary>Setup A levels: OrbPct stop and a RangePct target, both from <paramref name="entry"/>.</summary>
    public static (decimal stop, decimal target, decimal partial, decimal rr)
        CalcLevels(decimal entry, bool isLong, decimal stopPct,
                   int targetPct, int partialPct, decimal orbRange,
                   decimal tickSize = 0m)
    {
        decimal stop = RangeStop(entry, isLong, orbRange, stopPct, tickSize);
        var (target, partial, rr) = Targets(new LevelRequest(entry, isLong, stop, 1, TargetMode.RangePct,
            orbRange, targetPct, 0m, TargetDollarsBasis.PerContract, 0m, partialPct, tickSize));
        return (stop, target, partial, rr);
    }

    /// <summary>
    /// Setup B — stop is entry-anchored at entry ± orbRange * stopPct (tick-rounded).
    /// Default stopPct 0.50 gives the same stop as orbMid for a symmetric ORB.
    /// </summary>
    public static (decimal stop, decimal target, decimal partial, decimal rr)
        CalcLevelsB(decimal entry, bool isLong, int targetPct,
                    int partialPct, decimal orbRange, decimal stopPct,
                    decimal tickSize = 0m)
        => CalcLevels(entry, isLong, stopPct, targetPct, partialPct, orbRange, tickSize);

    private static decimal TargetDistance(LevelRequest r, decimal risk) => r.Mode switch
    {
        TargetMode.RangePct     => r.RangeOrAtr * (r.TargetPct / 100m),
        TargetMode.Dollars      => DollarDistance(r),
        TargetMode.Atr          => r.RangeOrAtr * r.Tp2Mult,
        TargetMode.RiskMultiple => risk * r.Tp2Mult,
        _                       => 0m,
    };

    /// <summary>Per contract: dollars / point value. Whole position: also spread over the contracts.</summary>
    private static decimal DollarDistance(LevelRequest r)
    {
        decimal perPoint = r.Basis == TargetDollarsBasis.WholePosition ? r.PointValue * r.Contracts : r.PointValue;
        return perPoint > 0 ? r.TargetDollars / perPoint : 0m;
    }

    private static decimal PartialDistance(LevelRequest r, decimal risk, decimal dist) => r.Mode switch
    {
        TargetMode.Atr          when r.Tp1Mult > 0 => r.RangeOrAtr * r.Tp1Mult,
        TargetMode.RiskMultiple when r.Tp1Mult > 0 => risk * r.Tp1Mult,
        _                                          => dist * (r.PartialPct / 100m),
    };

    /// <summary>The partial price <paramref name="partialDist"/> from the entry, on the tick. Every level goes through this rule.</summary>
    private static decimal PartialPrice(LevelRequest r, decimal partialDist)
        => RoundToTick(r.IsLong ? r.Entry + partialDist : r.Entry - partialDist, r.TickSize);

    private static decimal CeilToTick(decimal price, decimal tickSize)
        => tickSize > 0 ? Math.Ceiling(price / tickSize) * tickSize : price;

    private static decimal FloorToTick(decimal price, decimal tickSize)
        => tickSize > 0 ? Math.Floor(price / tickSize) * tickSize : price;
}

/// <summary>Result of one bar's exit processing.</summary>
public record ExitResult(
    bool    HitTarget,
    bool    HitStop,
    decimal NewPnl,
    bool    StillActive,
    bool    PartialHit,
    decimal NewStop);

/// <summary>Port of Pine f_processExit()</summary>
public static class ExitProcessor
{
    public static ExitResult ProcessBar(
        bool active, bool isLong,
        decimal entry, decimal stopLvl, decimal target, decimal partial,
        int contracts, decimal currentPnl, bool partialHit,
        bool usePartial, bool useBE, decimal pointValue,
        decimal barHigh, decimal barLow,
        int fixedPartialCts = 0)
    {
        if (!active) return new(false, false, currentPnl, false, partialHit, stopLvl);

        decimal newPnl  = currentPnl;
        decimal newStop = stopLvl;
        bool    newPart = partialHit;

        int half    = fixedPartialCts > 0
            ? Math.Min(fixedPartialCts, contracts - 1)
            : (int)Math.Floor(contracts * 0.5);
        int partCts = usePartial ? half              : 0;
        int remCts  = usePartial ? contracts - half  : contracts;

        bool targetHit = isLong ? barHigh >= target : barLow <= target;

        // Partial — only fire when there are actually contracts to partial
        if (usePartial && !partialHit && !targetHit && half > 0)
        {
            bool partCrossed = isLong ? barHigh >= partial : barLow <= partial;
            if (partCrossed)
            {
                newPart  = true;
                newPnl  += (isLong ? partial - entry : entry - partial) * pointValue * partCts;
                if (useBE) newStop = entry;
            }
        }

        // Full target
        if (targetHit)
        {
            newPnl += (isLong ? target - entry : entry - target) * pointValue * remCts;
            // Preserve newPart: partial may have fired on the same bar before target was hit.
            return new(true, false, newPnl, false, newPart, newStop);
        }

        // Stop
        bool stopHit = isLong ? barLow <= newStop : barHigh >= newStop;
        if (stopHit)
        {
            newPnl += (isLong ? newStop - entry : entry - newStop) * pointValue * remCts;
            // Preserve newPart: partial may have fired on the same bar (e.g. partial hit then
            // price returned to breakeven stop — both within a single bar).
            return new(false, true, newPnl, false, newPart, newStop);
        }

        return new(false, false, newPnl, true, newPart, newStop);
    }

    public static decimal ForcedExit(
        bool isLong, decimal entry, decimal closePrice,
        int contracts, bool partialHit, bool usePartial, decimal pointValue,
        int fixedPartialCts = 0)
    {
        int half   = fixedPartialCts > 0
            ? Math.Min(fixedPartialCts, contracts - 1)
            : (int)Math.Floor(contracts * 0.5);
        int remCts = (usePartial && partialHit && half > 0) ? contracts - half : contracts;
        return (isLong ? closePrice - entry : entry - closePrice) * pointValue * remCts;
    }
}
