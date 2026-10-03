using CRV.Core.Models;
using CRV.Core.Strategy;

namespace CRV.Web.Pages.Setup;

/// <summary>Plain-language descriptions of a basket strategy, for the list and editor.</summary>
public static class StrategyText
{
    public static string TypeName(StrategyType t) => t switch
    {
        StrategyType.Pullback       => "Pullback",
        StrategyType.Retest         => "Retest",
        StrategyType.OrbFakeout     => "Opening-range fakeout",
        StrategyType.SessionFakeout => "Session fakeout",
        StrategyType.Ema            => "EMA",
        SetupValidation.RetiredEma21 => "EMA21 (retired)",
        _                           => t.ToString(),
    };

    public static string Pct(decimal fraction) => (fraction * 100m).ToString("0.##") + "%";

    public static string Sessions(BasketEntry e)
    {
        if (e.Sessions is not { Count: > 0 }) return "all sessions";
        var on = e.Sessions.Where(s => s.Enabled).Select(s => $"{s.SessionId} until {s.CutoffHour:00}:{s.CutoffMinute:00}").ToList();
        return on.Count == 0 ? "no session" : string.Join(", ", on);
    }

    public static string Describe(BasketEntry e)
    {
        var c = e.Config;
        var what = e.StrategyType switch
        {
            StrategyType.Pullback       => $"After a break of the opening range, buys a pullback of {Pct(c.PullbackPct)} of the range back into it (or sells the mirror image)",
            StrategyType.Retest         => $"After a break of the opening range, enters on a retest of the range edge within {Pct(c.RetestPct)} of the range",
            StrategyType.OrbFakeout     => "Fades a false break of the opening range",
            StrategyType.SessionFakeout => $"Fades a false break of the previous session's range ({(c.FakeoutReferenceSession == FakeoutSession.Auto ? "whichever session just ended" : c.FakeoutReferenceSession.ToString())})",
            _                           => "Trades the setup",
        };
        var mode = c.Mode switch
        {
            "Aggressive"   => "enters as soon as the setup arms",
            "Conservative" => "waits for price to reach the entry level",
            _              => $"uses {c.Mode} entries",
        };
        var stop = c.StopMode switch
        {
            "BarHL" => "the signal bar's high or low",
            "Vwap"  => $"VWAP ± {c.StopVwapTicks} ticks",
            _       => $"{Pct(c.StopPct)} of the range from entry",
        };
        var exits = c.UsePartial
            ? $"Takes {(c.PartialCts > 0 ? c.PartialCts.ToString() : "half the")} contract{(c.PartialCts == 1 ? "" : "s")} off at {c.PartialPct}% of the way to target{(c.UseBe ? " and moves the stop to break-even" : "")}."
            : "Exits everything at the target.";
        var size = c.AutoSizeByRisk && c.MaxTradeRisk > 0
            ? $"Sizes {c.Contracts}–{c.MaxContracts} contracts to risk up to ${c.MaxTradeRisk:N0}."
            : $"Trades {c.Contracts} contract{(c.Contracts == 1 ? "" : "s")}{(c.MaxTradeRisk > 0 ? $", skipping trades that risk more than ${c.MaxTradeRisk:N0}" : "")}.";

        return $"{what} on {e.Ticker}, and {mode}. Stop is {stop}; target is {c.TargetPct}% of the range. {exits} {size} " +
               $"Up to {c.MaxTrades} trade{(c.MaxTrades == 1 ? "" : "s")} per session, in {Sessions(e)}.";
    }
}
