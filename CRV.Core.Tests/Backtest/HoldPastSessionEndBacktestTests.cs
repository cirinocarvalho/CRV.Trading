using CRV.Backtest.Engine;
using CRV.Backtest.Results;
using CRV.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CRV.Core.Tests.Backtest;

/// <summary>
/// Hold vs close through the backtest. The fixture's session fills a long at 18010 with its
/// stop at 18000. The target is moved out of reach and the day runs flat at 18100 to 16:30 ET,
/// past the 15:00 cutoff and the 16:00 session end.
/// </summary>
public class HoldPastSessionEndBacktestTests
{
    private static readonly DateTime DayOneOpen = PullbackSessionFixture.Open;   // 09:30 ET
    private static readonly DateTime DayTwoOpen = DayOneOpen.AddDays(1);

    private static async IAsyncEnumerable<(string Ticker, Bar Bar)> DayOneThen(IEnumerable<(string, Bar)> after)
    {
        await foreach (var b in PullbackSessionFixture.Session()) yield return b;
        for (int m = 120; m < 420; m++)
            yield return (PullbackSessionFixture.Ticker, new Bar(DayOneOpen.AddMinutes(m), 18100m, 18101m, 18099m, 18100m, 500));
        foreach (var b in after) yield return b;
    }

    /// <summary>18100 falling to 17990 over 30 minutes, then flat: through the 18000 stop.</summary>
    private static IEnumerable<(string, Bar)> FallThroughStop(DateTime start)
    {
        for (int i = 0; i < 30; i++)
        {
            decimal a = 18100m - 110m * i / 30, b = 18100m - 110m * (i + 1) / 30;
            yield return (PullbackSessionFixture.Ticker, new Bar(start.AddMinutes(i), a, a, b, b, 500));
        }
        for (int i = 30; i < 40; i++)
            yield return (PullbackSessionFixture.Ticker, new Bar(start.AddMinutes(i), 17990m, 17991m, 17989m, 17990m, 500));
    }

    /// <summary>Opens at 17900, already 100 points through the 18000 stop, then stays there.</summary>
    private static IEnumerable<(string, Bar)> GapBelowStop(DateTime start)
    {
        for (int i = 0; i < 10; i++)
            yield return (PullbackSessionFixture.Ticker, new Bar(start.AddMinutes(i), 17900m, 17901m, 17899m, 17900m, 500));
    }

    private static Task<BacktestResult> Run(bool closeAtRthClose, IAsyncEnumerable<(string, Bar)> bars)
    {
        var cfg = PullbackSessionFixture.Config(s => { s.TargetPct = 1000; s.CloseAtRthClose = closeAtRthClose; });
        var bt = PullbackSessionFixture.BtConfig();
        bt.To = bt.From.AddDays(3);
        return new BacktestEngine(cfg, bt, NullLogger<BacktestEngine>.Instance).RunAsync(bars);
    }

    [Fact]
    public async Task ClosingSetup_IsFlattenedAtItsCutoff()
    {
        var r = await Run(true, DayOneThen(FallThroughStop(DayTwoOpen)));

        var first = r.Trades[0];
        Assert.Equal(ExitReason.SessionEnd, first.ExitReason);
        Assert.InRange(first.ExitedAt, DayOneOpen.AddMinutes(330), DayOneOpen.AddMinutes(390));   // 15:00–16:00 ET
        Assert.InRange(first.Exit, 18099m, 18101m);
    }

    [Fact]
    public async Task HoldingSetup_KeepsItsTradeThroughSessionEnd_AndExitsOnItsStopNextSession()
    {
        var r = await Run(false, DayOneThen(FallThroughStop(DayTwoOpen)));

        var first = r.Trades[0];
        Assert.Equal(ExitReason.Stop, first.ExitReason);
        Assert.Equal(DayTwoOpen.Date, first.ExitedAt.Date);
        Assert.True(first.Exit <= 18000m, $"stop fill {first.Exit} should be at or below 18000");
        // Nothing else entered while the trade was held.
        Assert.Single(r.Trades);
        Assert.All(r.Trades.Skip(1), t => Assert.True(t.EnteredAt >= first.ExitedAt));
    }

    [Fact]
    public async Task HeldStop_FillsOvernight_WhenNoSessionIsRunning()
    {
        var evening = DayOneOpen.Date.AddHours(23);   // 19:00 ET; only NY is backtested
        var r = await Run(false, DayOneThen(FallThroughStop(evening)));

        var t = Assert.Single(r.Trades);
        Assert.Equal(ExitReason.Stop, t.ExitReason);
        Assert.InRange(t.ExitedAt, evening, evening.AddMinutes(40));
    }

    [Fact]
    public async Task HoldingSetup_StillOpenWhenDataEnds_IsClosedAtTheLastClose()
    {
        var r = await Run(false, DayOneThen(Array.Empty<(string, Bar)>()));

        var t = Assert.Single(r.Trades);
        Assert.Equal(ExitReason.SessionEnd, t.ExitReason);
        Assert.Equal(DayOneOpen.AddMinutes(419), t.ExitedAt);
        Assert.Equal(18100m, t.Exit);
    }

    [Fact]
    public async Task ClosingSetup_WhenDataEnds_HasNothingLeftToClose()
    {
        var r = await Run(true, DayOneThen(Array.Empty<(string, Bar)>()));

        var t = Assert.Single(r.Trades);
        Assert.InRange(t.ExitedAt, DayOneOpen.AddMinutes(330), DayOneOpen.AddMinutes(390));
    }

    [Fact]
    public async Task HeldStop_GappedThroughOvernight_FillsAtTheGapPrice()
    {
        var evening = DayOneOpen.Date.AddHours(23);   // 19:00 ET; only NY is backtested
        var r = await Run(false, DayOneThen(GapBelowStop(evening)));

        var t = Assert.Single(r.Trades);
        Assert.Equal((ExitReason.Stop, evening), (t.ExitReason, t.ExitedAt));
        Assert.InRange(t.Exit, 17890m, 17900m);
    }

    [Fact]
    public async Task HeldStop_GappedThroughAtTheSessionOpen_FillsAtTheGapPrice()
    {
        var r = await Run(false, DayOneThen(GapBelowStop(DayTwoOpen)));

        var t = Assert.Single(r.Trades);
        Assert.Equal((ExitReason.Stop, DayTwoOpen), (t.ExitReason, t.ExitedAt));
        Assert.InRange(t.Exit, 17890m, 17900m);
    }

    [Fact]
    public async Task ClosingSetup_TradesAreUnchangedByAGapAtTheNextOpen()
    {
        var gapped = await Run(true, DayOneThen(GapBelowStop(DayTwoOpen)));
        var flat   = await Run(true, DayOneThen(Array.Empty<(string, Bar)>()));

        Assert.Equal(
            flat.Trades.Select(t => (t.EnteredAt, t.Entry, t.ExitedAt, t.Exit, t.ExitReason)),
            gapped.Trades.Select(t => (t.EnteredAt, t.Entry, t.ExitedAt, t.Exit, t.ExitReason)));
    }
}
