using System.Text.Json;
using CRV.Core.Models;

namespace CRV.Web.A11yTests;

/// <summary>
/// An engine snapshot with one setup card in each state the cockpit draws while the engine runs
/// (idle, armed, retest, cutoff, max trades, long and short in a trade), serialized the way the
/// SignalR hub sends it (camelCase, enums as numbers), so the scan sees the cards a running engine shows.
/// </summary>
public static class CockpitSnapshot
{
    /// <summary>The status badge each card shows, in snapshot order.</summary>
    public static readonly string[] ExpectedStatuses =
    [
        "IDLE", "▶ ARMED LONG", "↩ RETEST SHORT", "CUTOFF", "MAX TRADES",
        "● LONG ACTIVE [PARTIALFILLED]", "● SHORT ACTIVE [FILLED]",
    ];

    public static string Json() => Json(EveryState());

    /// <summary>A running-engine snapshot holding just <paramref name="setups"/>.</summary>
    public static string Json(IEnumerable<SetupSnapshot> setups)
    {
        var now = DateTime.UtcNow;
        var snap = new EngineSnapshot
        {
            Time = now, LastUpdate = now, Ticker = "/MNQZ26", IsLive = true,
            LastPrice = 21236.25m, TodayPnl = 212.5m, TodayTrades = 2, TodayWins = 1, TodayLosses = 1,
            DailyLossLimit = 500m, DailyLossUsed = 120m, ActiveSessionId = "NY", OrbFormed = true,
            OrbHigh = 21250m, OrbLow = 21180m, OrbMid = 21215m, OrbRange = 70m,
            Setups = setups.ToList(),
        };
        return JsonSerializer.Serialize(snap, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    }

    private static IEnumerable<SetupSnapshot> EveryState()
    {
        var now = DateTime.UtcNow;
        return
        [
            Setup("a11y-idle", "Pullback [MNQ]", "Pullback", state: 0),
            Setup("a11y-armed", "Pullback [MES]", "Pullback", state: 1),
            Setup(A11ySeed.RetestId, "Retest [MNQ]", "Retest", state: -2),
            Setup("a11y-cutoff", "ORB fakeout [MNQ]", "OrbFakeout", state: 0, pastCutoff: true),
            Setup("a11y-maxtrades", "Session fakeout [MNQ]", "SessionFakeout", state: 0, tradeCount: 2),
            Setup("a11y-long", "ORB fakeout [MES]", "OrbFakeout", state: 0, trade: new ActiveTradeView
            {
                Setup = SetupId.F, Direction = Direction.Long, Entry = 21200m, InitialStop = 21180m,
                CurrentStop = 21200m, Target = 21260m, Partial = 21230m, Contracts = 2, PartialContracts = 1,
                RemainingContracts = 1, PartialFilled = true, LastPrice = 21236.25m, UnrealizedPnl = 72.5m,
                EnteredAt = now.AddMinutes(-20), Ticker = "/MESZ26", PointValue = 2m, GroupStatus = "PartialFilled",
            }),
            Setup("a11y-short", "Session fakeout [MES]", "SessionFakeout", state: 0, trade: new ActiveTradeView
            {
                Setup = SetupId.F, Direction = Direction.Short, Entry = 21250m, InitialStop = 21270m,
                CurrentStop = 21270m, Target = 21210m, Contracts = 1, RemainingContracts = 1,
                LastPrice = 21236.25m, UnrealizedPnl = 27.5m, EnteredAt = now.AddMinutes(-5), Ticker = "/MESZ26",
                PointValue = 2m, GroupStatus = "Filled",
            }),
        ];
    }

    public static SetupSnapshot Setup(string id, string label, string type, int state,
        bool pastCutoff = false, int tradeCount = 0, ActiveTradeView? trade = null) => new()
    {
        Id = id, Label = label, StrategyType = type, Ticker = "/MNQZ26", PointValue = 2m, LastPrice = 21236.25m,
        Enabled = true, State = state, PastCutoff = pastCutoff, Trade = trade, TradeCount = trade == null ? tradeCount : 1,
        MaxTrades = 2, Wins = 1, Losses = 1, WinPnl = 140m, LossPnl = -40m, Expectancy = 50m,
        OrbHigh = 21250m, OrbLow = 21180m, OrbMid = 21215m, OrbRange = 70m, OrbFormed = true,
    };
}
