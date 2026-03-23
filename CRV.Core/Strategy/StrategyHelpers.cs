namespace CRV.Core.Strategy;

/// <summary>Port of Pine f_calcLevels() — Setup A (pct-based stop)</summary>
public static class LevelCalculator
{
    /// <summary>
    /// Rounds <paramref name="price"/> to the nearest valid tick.
    /// tickSize = 0 means no rounding (used by existing tests without a tick parameter).
    /// </summary>
    public static decimal RoundToTick(decimal price, decimal tickSize)
        => tickSize > 0 ? Math.Round(price / tickSize, MidpointRounding.AwayFromZero) * tickSize : price;

    public static (decimal stop, decimal target, decimal partial, decimal rr)
        CalcLevels(decimal entry, bool isLong, decimal stopPct,
                   int targetPct, int partialPct, decimal orbRange,
                   decimal tickSize = 0m)
    {
        decimal stopDist    = orbRange * stopPct;
        decimal targetDist  = orbRange * (targetPct  / 100m);
        decimal partialDist = targetDist * (partialPct / 100m);

        decimal stop, target, partial;
        if (isLong) { stop = entry - stopDist; target = entry + targetDist; partial = entry + partialDist; }
        else        { stop = entry + stopDist; target = entry - targetDist; partial = entry - partialDist; }

        // Snap to nearest valid tick (no-op when tickSize == 0)
        stop    = RoundToTick(stop,    tickSize);
        target  = RoundToTick(target,  tickSize);
        partial = RoundToTick(partial, tickSize);

        decimal risk   = Math.Abs(entry - stop);
        decimal reward = Math.Abs(target - entry);
        decimal rr     = risk > 0 ? reward / risk : 0;
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
    {
        decimal stopDist    = orbRange * stopPct;
        decimal targetDist  = orbRange * (targetPct  / 100m);
        decimal partialDist = targetDist * (partialPct / 100m);

        decimal stop    = RoundToTick(isLong ? entry - stopDist  : entry + stopDist,  tickSize);
        decimal target  = RoundToTick(isLong ? entry + targetDist : entry - targetDist, tickSize);
        decimal partial = RoundToTick(isLong ? entry + partialDist : entry - partialDist, tickSize);

        decimal risk   = Math.Abs(entry - stop);
        decimal reward = Math.Abs(target - entry);
        decimal rr     = risk > 0 ? reward / risk : 0;
        return (stop, target, partial, rr);
    }
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
