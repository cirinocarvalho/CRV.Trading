using CRV.Core.Models;
using CRV.Core.Strategy;
using Xunit;

namespace CRV.Core.Tests.Models;

/// <summary>How StrategyConfig reads its two baskets: validation, unreadable JSON, and the bar size a feed runs on.</summary>
public class BasketConfigTests
{
    private static BasketEntry Entry(string id, StrategyType type, string ticker, int? barMinutes = null, bool enabled = true) => new()
    {
        Id = id, Label = id, Enabled = enabled, StrategyType = type, Ticker = ticker,
        PointValue = 2m, TickSize = 0.25m, ExecutionTFMinutes = barMinutes,
    };

    private static string Basket(params BasketEntry[] entries) => BasketCodec.Serialize(entries);

    // ── Validate ────────────────────────────────────────────────

    [Fact]
    public void Validate_ChecksTheEmaBasketToo()
    {
        var cfg = new StrategyConfig { EmaBasketJson = Basket(Entry("ema21-mnq", SetupValidation.RetiredEma21, "/MNQZ26")) };

        Assert.Contains("Basket entry 'ema21-mnq': retired EMA21 strategy.", cfg.Validate());
    }

    [Fact]
    public void Validate_SwitchedOffEntry_ReportsNothing()
    {
        var cfg = new StrategyConfig { EmaBasketJson = Basket(Entry("ema21-mnq", SetupValidation.RetiredEma21, "/MNQZ26", enabled: false)) };

        Assert.DoesNotContain(cfg.Validate(), e => e.StartsWith("Basket entry"));
    }

    [Fact]
    public void Validate_ReportsMismatchedBarSizesOnARoot()
    {
        var cfg = new StrategyConfig
        {
            BasketJson = Basket(Entry("pullback-mnq", StrategyType.Pullback, "/MNQZ26", 5), Entry("retest-nq", StrategyType.Retest, "/NQZ26", 15)),
        };

        Assert.Contains("Every NQ / MNQ strategy must use one bar size: pullback-mnq 5 min, retest-nq 15 min.", cfg.Validate());
    }

    [Fact]
    public void Validate_ReportsAnUnreadableBasket()
    {
        var cfg = new StrategyConfig { BasketJson = "not json {{" };

        Assert.Contains(cfg.Validate(), e => e.StartsWith("The opening-range strategy list can't be read"));
    }

    // ── One unreadable basket doesn't take the other down ─────────

    [Fact]
    public void ToSetupConfigs_UnreadableEmaBasket_KeepsOrbSetups()
    {
        var cfg = new StrategyConfig { BasketJson = Basket(Entry("pullback-mnq", StrategyType.Pullback, "/MNQZ26")), EmaBasketJson = "[{" };

        Assert.Equal(new[] { "pullback-mnq" }, cfg.ToSetupConfigs().Select(s => s.Id));
    }

    [Fact]
    public void ToSetupConfigs_UnreadableOrbBasket_KeepsEmaSetups()
    {
        var cfg = new StrategyConfig { BasketJson = "not json {{", EmaBasketJson = Basket(Entry("pullback-nq", StrategyType.Pullback, "/NQZ26")) };

        Assert.Equal(new[] { "pullback-nq" }, cfg.ToSetupConfigs().Select(s => s.Id));
    }

    // ── TfMinutesFor ────────────────────────────────────────────

    private static StrategyConfig NqRootAt15() => new()
    {
        ExecutionTFMinutes = 1,
        BasketJson = Basket(
            Entry("ema21-mnq", SetupValidation.RetiredEma21, "/MNQZ26", 5),
            Entry("off-mnq", StrategyType.Pullback, "/MNQZ26", 30, enabled: false),
            Entry("retest-nq", StrategyType.Retest, "/NQZ26", 15)),
    };

    [Fact]
    public void TfMinutesFor_ResolvesByRoot_FromEntriesThatTrade()
    {
        var cfg = NqRootAt15();

        Assert.Equal(15, cfg.TfMinutesFor("/MNQZ26"));
        Assert.Equal(15, cfg.TfMinutesFor("MNQZ6"));     // Tradovate's format
        Assert.Equal(15, cfg.TfMinutesFor("/NQZ26"));
    }

    [Fact]
    public void TfMinutesFor_RootWithoutEntriesThatTrade_UsesTheFallback()
    {
        var cfg = NqRootAt15();

        Assert.Equal(1, cfg.TfMinutesFor("/ESZ26"));
        Assert.Equal(5, cfg.TfMinutesFor("/ESZ26", 5));
    }

    [Fact]
    public void TfMinutesFor_EntryWithoutItsOwnBarSize_UsesTheFallback()
    {
        var cfg = new StrategyConfig { ExecutionTFMinutes = 1, BasketJson = Basket(Entry("retest-nq", StrategyType.Retest, "/NQZ26")) };

        Assert.Equal(10, cfg.TfMinutesFor("/MNQZ26", 10));
    }

    // ── ToSetupConfigsWithoutSkipped ────────────────────────────

    [Fact]
    public void ToSetupConfigsWithoutSkipped_LeavesOutSwitchedOnEntriesTheEngineSkips()
    {
        var cfg = new StrategyConfig
        {
            ExecutionTFMinutes = 1,
            BasketJson = Basket(
                Entry("pullback-mnq", StrategyType.Pullback, "/MNQZ26", 15),
                Entry("retest-nq", StrategyType.Retest, "/NQZ26", 5),
                Entry("off-es", StrategyType.Pullback, "/ESZ26", 1, enabled: false)),
            EmaBasketJson = Basket(Entry("ema21-mes", SetupValidation.RetiredEma21, "/MESZ26", 1)),
        };

        var setups = cfg.ToSetupConfigsWithoutSkipped();

        Assert.Equal(new[] { "pullback-mnq", "off-es" }, setups.Select(s => s.Id));
        Assert.Equal(new[] { "/MNQZ26" }, setups.Where(s => s.Enabled).Select(s => s.Ticker));
    }
}
