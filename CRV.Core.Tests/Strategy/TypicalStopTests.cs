using System.Text.Json;
using CRV.Backtest.Results;
using CRV.Core.Models;
using CRV.Core.Strategy;
using Xunit;

namespace CRV.Core.Tests.Strategy;

public class TypicalStopTests
{
    private static readonly DateTime Day = new(2026, 4, 15, 14, 0, 0, DateTimeKind.Utc);

    private static TradeRecord Trade(decimal stopPts, int daysAgo = 0, int contracts = 1, string label = "of-mnq") => new()
    {
        SetupLabel = label, Ticker = "MNQZ26", Direction = Direction.Long, Contracts = contracts,
        Entry = 20000m, InitialStop = 20000m - stopPts, Target = 20000m + 2 * stopPts,
        EnteredAt = Day.AddDays(-daysAgo), ExitedAt = Day.AddDays(-daysAgo).AddMinutes(30),
    };

    [Fact]
    public void Median_OddCount_IsTheMiddleStop()
        => Assert.Equal(20m, TypicalStop.Median([Trade(10m, 1), Trade(40m, 2), Trade(20m, 3)]));

    [Fact]
    public void Median_EvenCount_AveragesTheMiddleTwo()
        => Assert.Equal(25m, TypicalStop.Median([Trade(10m, 1), Trade(20m, 2), Trade(30m, 3), Trade(40m, 4)]));

    [Fact]
    public void Median_UsesOnlyTheLast30Trades()
    {
        var trades = Enumerable.Range(1, 30).Select(d => Trade(10m, d)).Append(Trade(1000m, daysAgo: 99));

        Assert.Equal(10m, TypicalStop.Median(trades));
    }

    [Fact]
    public void Median_CountsATradeSavedInTwoRunsOnce()
    {
        // The same trade from two overlapping runs, plus one other: [10, 40], not [10, 10, 40].
        Assert.Equal(25m, TypicalStop.Median([Trade(10m, 1), Trade(10m, 1), Trade(40m, 2)]));
    }

    [Fact]
    public void Median_NoUsableTrades_IsNull()
    {
        var noStop = Trade(10m);
        noStop.InitialStop = 0m;

        Assert.Null(TypicalStop.Median([]));
        Assert.Null(TypicalStop.Median([noStop]));
    }

    [Fact]
    public void MedianPosition_MultipliesByContracts()
        => Assert.Equal(40m, TypicalStop.MedianPosition([Trade(40m, 1, contracts: 2), Trade(40m, 2), Trade(10m, 3, contracts: 4)]));

    [Fact]
    public void FromRuns_KeepsThisSetupsTrades_AndSkipsUnreadableRuns()
    {
        string Run(params TradeRecord[] t) => JsonSerializer.Serialize(new BacktestResult { Trades = t.ToList() });

        var trades = TypicalStop.FromRuns(
            [Run(Trade(10m, 1), Trade(20m, 2, label: "other")), null, "{not json", Run(Trade(30m, 3))], "of-mnq");

        Assert.Equal(new[] { 10m, 30m }, trades.Select(t => t.Entry - t.InitialStop).OrderBy(x => x));
    }

    [Fact]
    public void FromRuns_StopsReadingOnceItHasEnoughTrades()
    {
        string Run(params TradeRecord[] t) => JsonSerializer.Serialize(new BacktestResult { Trades = t.ToList() });
        var firstRun = Run(Enumerable.Range(1, 30).Select(d => Trade(10m, d)).ToArray());
        var readRuns = 0;

        IEnumerable<string?> Runs()
        {
            readRuns++;
            yield return firstRun;
            readRuns++;
            yield return Run(Trade(99m, 40));
        }

        var trades = TypicalStop.FromRuns(Runs(), "of-mnq");

        Assert.Equal(30, trades.Count);
        Assert.Equal(1, readRuns);
    }

    [Fact]
    public void FromRuns_KeepsReadingWhileOverlappingRunsAddNothingNew()
    {
        string Run(params TradeRecord[] t) => JsonSerializer.Serialize(new BacktestResult { Trades = t.ToList() });
        var same = Run(Enumerable.Range(1, 20).Select(d => Trade(10m, d)).ToArray());

        var trades = TypicalStop.FromRuns([same, same, Run(Trade(50m, 30))], "of-mnq");

        Assert.Equal(21, TypicalStop.Recent(trades).Count);
    }
}
