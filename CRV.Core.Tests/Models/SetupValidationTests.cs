using CRV.Core.Models;
using CRV.Core.Strategy;
using Xunit;

namespace CRV.Core.Tests.Models;

public class SetupValidationTests
{
    private static BasketEntry Entry(string id, StrategyType type, string ticker, int? barMinutes = null, bool enabled = true) => new()
    {
        Id = id, Label = id, Enabled = enabled, StrategyType = type, Ticker = ticker,
        PointValue = 2m, TickSize = 0.25m, ExecutionTFMinutes = barMinutes,
    };

    private static StrategyConfig Config(IEnumerable<BasketEntry> orb, IEnumerable<BasketEntry>? ema = null) => new()
    {
        ExecutionTFMinutes = 1,
        BasketJson    = BasketCodec.Serialize(orb.ToList()),
        EmaBasketJson = BasketCodec.Serialize((ema ?? Array.Empty<BasketEntry>()).ToList()),
    };

    private const string NqBarConflict =
        "bar size 5 min differs from the 15 min retest-nq uses; every NQ / MNQ strategy shares one bar size";

    // ── Entry ───────────────────────────────────────────────────

    [Fact]
    public void Entry_RetiredEma21_IsReportedAsRetired()
        => Assert.Equal(new[] { "retired EMA21 strategy" },
            SetupValidation.Entry(Entry("ema21-mnq", SetupValidation.RetiredEma21, "/MNQZ26"), Config([])));

    [Fact]
    public void Entry_UnknownType_IsReportedWithItsNumber()
        => Assert.Contains("unknown strategy type 9",
            SetupValidation.Entry(Entry("x", (StrategyType)9, "/MNQZ26"), Config([])));

    [Fact]
    public void Entry_NoTicker_IsReportedAsHavingNoInstrument()
        => Assert.Contains("it has no instrument",
            SetupValidation.Entry(Entry("pullback-x", StrategyType.Pullback, null!), Config([])));

    [Fact]
    public void EnabledEntryWithNoTicker_IsDisabledAndDoesntBreakTheRootChecks()
    {
        var cfg = Config([Entry("pullback-x", StrategyType.Pullback, null!),
                          Entry("pullback-mnq", StrategyType.Pullback, "/MNQZ26", 5)]);

        var disabled = SetupValidation.DisabledSetups(cfg);

        Assert.Equal("it has no instrument", Assert.Single(disabled).Reason);
        Assert.Empty(SetupValidation.RootBarSizes(cfg));
        Assert.Equal(5, cfg.TfMinutesFor("/MNQZ26"));
        Assert.Equal(new[] { "it has no instrument" }, SetupValidation.SaveErrors(Entry("pullback-x", StrategyType.Pullback, null!), cfg));
    }

    [Fact]
    public void Entry_EmaType_CantRunYet()
        => Assert.Contains("the EMA strategy can't run in this version",
            SetupValidation.Entry(Entry("ema-mnq", StrategyType.Ema, "/MNQZ26"), Config([])));

    [Fact]
    public void Entry_KnownTypeOnAListedBarSize_HasNoProblems()
        => Assert.Empty(SetupValidation.Entry(Entry("pullback-mnq", StrategyType.Pullback, "/MNQZ26", 5), Config([])));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Entry_NonPositiveBarSize_MeansNoOverrideAndUsesTheConfigs(int barMinutes)
    {
        var e = Entry("p", StrategyType.Pullback, "/MNQZ26", barMinutes);
        var cfg = Config([e]);
        cfg.ExecutionTFMinutes = 5;

        Assert.Empty(SetupValidation.Entry(e, cfg));
        Assert.Equal(5, SetupValidation.BarMinutes(e, cfg));
    }

    [Fact]
    public void Entry_BarSizeOffTheList_IsReported()
        => Assert.Contains("bar size 7 min isn't one of 1, 2, 5, 10, 15, 20, 30, 60 min",
            SetupValidation.Entry(Entry("p", StrategyType.Pullback, "/MNQZ26", 7), Config([])));

    [Fact]
    public void Entry_OwnRangeEndingBeforeItStarts_IsReported()
    {
        var e = Entry("p", StrategyType.Pullback, "/MNQZ26");
        e.Config.UseCustomOrbWindow = true;
        e.Config.OrbStart = new TimeOnly(10, 0);
        e.Config.OrbEnd = new TimeOnly(9, 30);

        Assert.Contains("its opening range ends before it starts", SetupValidation.Entry(e, Config([])));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-50)]
    public void Entry_DollarsTargetNotAboveZero_IsReported(decimal dollars)
    {
        var e = Entry("p", StrategyType.Pullback, "/MNQZ26");
        e.Config.TargetMode = TargetMode.Dollars;
        e.Config.TargetDollars = dollars;

        Assert.Contains("dollars target must be above 0", SetupValidation.Entry(e, Config([])));
    }

    [Fact]
    public void Entry_ZeroDollarsWhileTargetIsAShareOfTheRange_HasNoProblems()
    {
        var e = Entry("p", StrategyType.Pullback, "/MNQZ26");
        e.Config.TargetMode = TargetMode.RangePct;
        e.Config.TargetDollars = 0;

        Assert.Empty(SetupValidation.Entry(e, Config([])));
    }

    [Fact]
    public void Entry_DollarsTargetAboveZero_HasNoProblems()
    {
        var e = Entry("p", StrategyType.Pullback, "/MNQZ26");
        e.Config.TargetMode = TargetMode.Dollars;
        e.Config.TargetDollars = 400m;

        Assert.Empty(SetupValidation.Entry(e, Config([])));
    }

    [Fact]
    public void Entry_UndefinedMinRrAction_IsReported()
    {
        var e = Entry("p", StrategyType.Pullback, "/MNQZ26");
        e.Config.MinRrAction = (MinRrAction)7;

        Assert.Contains("minimum R action isn't a known choice", SetupValidation.Entry(e, Config([])));
    }

    [Fact]
    public void Entry_UndefinedTargetMode_IsReported()
    {
        var e = Entry("p", StrategyType.Pullback, "/MNQZ26");
        e.Config.TargetMode = (TargetMode)9;

        Assert.Contains("target mode isn't a known choice", SetupValidation.Entry(e, Config([])));
    }

    [Fact]
    public void Entry_UndefinedTargetDollarsBasis_IsReported()
    {
        var e = Entry("p", StrategyType.Pullback, "/MNQZ26");
        e.Config.TargetDollarsBasis = (TargetDollarsBasis)5;

        Assert.Contains("dollars basis isn't a known choice", SetupValidation.Entry(e, Config([])));
    }

    [Fact]
    public void Entry_EveryDefinedTargetChoice_HasNoProblems()
    {
        var e = Entry("p", StrategyType.Pullback, "/MNQZ26");
        e.Config.TargetDollars = 400m;
        foreach (var mode in Enum.GetValues<TargetMode>())
        foreach (var basis in Enum.GetValues<TargetDollarsBasis>())
        foreach (var action in Enum.GetValues<MinRrAction>())
        {
            e.Config.TargetMode = mode;
            e.Config.TargetDollarsBasis = basis;
            e.Config.MinRrAction = action;
            Assert.Empty(SetupValidation.Entry(e, Config([])));
        }
    }

    [Fact]
    public void Entry_NegativeTargetPct_IsReported()
    {
        var e = Entry("p", StrategyType.Pullback, "/MNQZ26");
        e.Config.TargetPct = -10;

        Assert.Contains("target can't be a negative share of the range", SetupValidation.Entry(e, Config([])));
    }

    [Fact]
    public void Entry_NegativeFirstTargetMultiple_IsReported()
    {
        var e = Entry("p", StrategyType.Pullback, "/MNQZ26");
        e.Config.AtrTp1Mult = -1m;

        Assert.Contains("first target multiple can't be negative", SetupValidation.Entry(e, Config([])));
    }

    [Fact]
    public void Entry_NegativeSecondTargetMultiple_IsReported()
    {
        var e = Entry("p", StrategyType.Pullback, "/MNQZ26");
        e.Config.AtrTp2Mult = -2m;

        Assert.Contains("second target multiple can't be negative", SetupValidation.Entry(e, Config([])));
    }

    [Fact]
    public void Entry_ZeroTargetPctAndMultiples_AreNotNegative()
    {
        var e = Entry("p", StrategyType.Pullback, "/MNQZ26");
        e.Config.TargetMode = TargetMode.RiskMultiple;
        e.Config.TargetPct = 0;
        e.Config.AtrTp1Mult = 0m;
        e.Config.AtrTp2Mult = 0m;

        Assert.Empty(SetupValidation.Entry(e, Config([])));
    }

    // ── RootBarSizes ────────────────────────────────────────────

    [Fact]
    public void RootBarSizes_MnqAt5WithNqAt15_IsRejected()
    {
        var cfg = Config([Entry("pullback-mnq", StrategyType.Pullback, "/MNQZ26", 5), Entry("retest-nq", StrategyType.Retest, "/NQZ26", 15)]);

        Assert.Equal("Every NQ / MNQ strategy must use one bar size: pullback-mnq 5 min, retest-nq 15 min.",
            Assert.Single(SetupValidation.RootBarSizes(cfg)));
    }

    [Fact]
    public void RootBarSizes_SameMismatchWithOneEntryOff_IsAccepted()
        => Assert.Empty(SetupValidation.RootBarSizes(Config([
            Entry("pullback-mnq", StrategyType.Pullback, "/MNQZ26", 5),
            Entry("retest-nq", StrategyType.Retest, "/NQZ26", 15, enabled: false)])));

    [Fact]
    public void RootBarSizes_OrbBasketAgainstEmaBasket_IsRejected()
    {
        // The EMA basket's own types can't run yet, so an opening-range type stands in to prove the check spans both lists.
        var cfg = Config([Entry("pullback-mnq", StrategyType.Pullback, "/MNQZ26", 5)],
                         [Entry("pullback-nq", StrategyType.Pullback, "/NQZ26", 15)]);

        Assert.Single(SetupValidation.RootBarSizes(cfg));
    }

    [Fact]
    public void RootBarSizes_EntryWithoutItsOwnBarSize_UsesTheConfigs()
    {
        var cfg = Config([Entry("a", StrategyType.Pullback, "/MNQZ26"), Entry("b", StrategyType.Retest, "/NQZ26", 1)]);
        Assert.Empty(SetupValidation.RootBarSizes(cfg));

        cfg.ExecutionTFMinutes = 5;
        Assert.Single(SetupValidation.RootBarSizes(cfg));
    }

    [Fact]
    public void RootBarSizes_DifferentRoots_DontConflict()
        => Assert.Empty(SetupValidation.RootBarSizes(Config([
            Entry("retest-mes", StrategyType.Retest, "/MESZ26", 5),
            Entry("retest-nq", StrategyType.Retest, "/NQZ26", 15)])));

    // ── DisabledSetups ──────────────────────────────────────────

    [Fact]
    public void DisabledSetups_FirstTradableEntryOnARootSetsItsBarSize()
    {
        var cfg = Config(
            [Entry("retest-nq", StrategyType.Retest, "/NQZ26", 15),
             Entry("pullback-mnq", StrategyType.Pullback, "/MNQZ26", 5),
             Entry("off-mnq", StrategyType.Pullback, "/MNQZ26", 1, enabled: false)],
            [Entry("ema21-mnq", SetupValidation.RetiredEma21, "/MNQZ26", 5)]);

        var disabled = SetupValidation.DisabledSetups(cfg).ToDictionary(d => d.Id, d => d.Reason);

        Assert.Equal(new[] { "ema21-mnq", "pullback-mnq" }, disabled.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal("retired EMA21 strategy", disabled["ema21-mnq"]);
        Assert.Equal(NqBarConflict, disabled["pullback-mnq"]);
    }

    [Fact]
    public void DisabledSetups_EntryThatCantTrade_DoesntClaimTheRoot()
    {
        var cfg = Config([Entry("ema21-mnq", SetupValidation.RetiredEma21, "/MNQZ26", 5),
                          Entry("pullback-mnq", StrategyType.Pullback, "/MNQZ26", 15)]);

        var only = Assert.Single(SetupValidation.DisabledSetups(cfg));

        Assert.Equal(("ema21-mnq", "ema21-mnq", SetupValidation.RetiredEma21, "/MNQZ26"), (only.Id, only.Label, only.Type, only.Ticker));
    }

    // ── SaveErrors ──────────────────────────────────────────────

    [Fact]
    public void SaveErrors_SwitchingOnAnEntryOnAnotherBarSize_IsRefused()
    {
        // The entry being switched on comes first in the list; it's still the one refused.
        var cfg = Config([Entry("pullback-mnq", StrategyType.Pullback, "/MNQZ26", 5, enabled: false),
                          Entry("retest-nq", StrategyType.Retest, "/NQZ26", 15)]);

        Assert.Equal(new[] { NqBarConflict },
            SetupValidation.SaveErrors(Entry("pullback-mnq", StrategyType.Pullback, "/MNQZ26", 5), cfg));
    }

    [Fact]
    public void SaveErrors_EntrySwitchedOff_SkipsTheRootCheck()
        => Assert.Empty(SetupValidation.SaveErrors(
            Entry("pullback-mnq", StrategyType.Pullback, "/MNQZ26", 5, enabled: false),
            Config([Entry("retest-nq", StrategyType.Retest, "/NQZ26", 15)])));

    [Fact]
    public void SaveErrors_ComparesAgainstOtherEntriesOnly()
        => Assert.Empty(SetupValidation.SaveErrors(
            Entry("retest-nq", StrategyType.Retest, "/NQZ26", 5),
            Config([Entry("retest-nq", StrategyType.Retest, "/NQZ26", 15)])));

    [Fact]
    public void SaveErrors_IgnoresOtherEntriesThatCantTrade()
        => Assert.Empty(SetupValidation.SaveErrors(
            Entry("pullback-mnq", StrategyType.Pullback, "/MNQZ26", 5),
            Config([], [Entry("ema21-mnq", SetupValidation.RetiredEma21, "/MNQZ26", 15)])));

    // ── DisabledReason ──────────────────────────────────────────

    [Fact]
    public void DisabledReason_RetiredEntrySwitchedOff_StillSaysRetired()
    {
        var retired = Entry("ema21-mnq", SetupValidation.RetiredEma21, "/MNQZ26", enabled: false);

        Assert.Equal("retired EMA21 strategy", SetupValidation.DisabledReason(retired, Config([], [retired])));
    }

    [Fact]
    public void DisabledReason_EntryThatTrades_IsNull()
    {
        var e = Entry("pullback-mnq", StrategyType.Pullback, "/MNQZ26", 5);

        Assert.Null(SetupValidation.DisabledReason(e, Config([e])));
    }

    // ── BasketErrors ────────────────────────────────────────────

    [Fact]
    public void BasketErrors_UnreadableOrbList_NamesIt()
        => Assert.StartsWith("The opening-range strategy list can't be read, so none of its strategies trade",
            Assert.Single(SetupValidation.BasketErrors(new StrategyConfig { BasketJson = "not json {{", EmaBasketJson = "" })));

    [Fact]
    public void BasketErrors_UnreadableEmaList_NamesIt()
        => Assert.StartsWith("The EMA strategy list can't be read, so none of its strategies trade",
            Assert.Single(SetupValidation.BasketErrors(new StrategyConfig { BasketJson = "", EmaBasketJson = "[{" })));

    [Fact]
    public void BasketErrors_ReadableLists_None()
        => Assert.Empty(SetupValidation.BasketErrors(Config([Entry("p", StrategyType.Pullback, "/MNQZ26")])));

    [Fact]
    public void Sentences_CapitalisesAndEndsEachProblem()
        => Assert.Equal("Retired EMA21 strategy. Its opening range ends before it starts.",
            SetupValidation.Sentences(["retired EMA21 strategy", "its opening range ends before it starts"]));
}
