using CRV.Core.Options;
using Xunit;

namespace CRV.Core.Tests.Options;

/// <summary>
/// Parsed against a fixture cut from a real Schwab chain response (SPY, 2026-08-28
/// expiry, underlying 766.08). Four contracts: a deep-ITM call, an ATM call, a
/// penny call with no bid, and a liquid put.
/// </summary>
public class OptionChainParserTests
{
    private static OptionChain Chain() =>
        OptionChainParser.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Options", "spy_chain_fixture.json")));

    private static OptionContract BySymbol(string s) =>
        Chain().Contracts.Single(c => c.Symbol == s);

    private const string AtmCall   = "SPY   260828C00767000";
    private const string PennyCall = "SPY   260828C00810000";
    private const string LiquidPut = "SPY   260828P00760000";

    [Fact]
    public void Parse_ReadsUnderlyingAndPrice()
    {
        var chain = Chain();
        Assert.Equal("SPY", chain.Underlying);
        Assert.Equal(766.08m, chain.UnderlyingPrice);
    }

    [Fact]
    public void Parse_ReadsBothCallsAndPuts()
    {
        var chain = Chain();
        Assert.Equal(3, chain.Contracts.Count(c => c.Right == OptionRight.Call));
        Assert.Equal(1, chain.Contracts.Count(c => c.Right == OptionRight.Put));
    }

    [Fact]
    public void Parse_CarriesOsiSymbolVerbatimIncludingPadding()
    {
        // Three spaces between root and date. A trimmed symbol is a different order.
        Assert.Equal(AtmCall, BySymbol(AtmCall).Symbol);
    }

    [Fact]
    public void Parse_ReadsExpirationAndDaysToExpiration()
    {
        var c = BySymbol(AtmCall);
        Assert.Equal(new DateTime(2026, 8, 28), c.Expiration.Date);
        Assert.Equal(2, c.DaysToExpiration);
    }

    [Fact]
    public void Parse_ReadsStrikeAndQuote()
    {
        var c = BySymbol(AtmCall);
        Assert.Equal(767m,  c.Strike);
        Assert.Equal(3.53m, c.Bid);
        Assert.Equal(3.56m, c.Ask);
    }

    [Fact]
    public void Parse_ReadsGreeksFromResponseRatherThanComputingThem()
    {
        var c = BySymbol(AtmCall);
        Assert.Equal(0.513m, c.Delta);
        Assert.True(c.Theta < 0m);
        Assert.True(c.ImpliedVolatility > 0m);
    }

    [Fact]
    public void Parse_ReadsMultiplierFromTheContractNotAConstant()
        => Assert.Equal(100, BySymbol(AtmCall).Multiplier);

    [Fact]
    public void Parse_ToleratesNaNGreeks()
    {
        // Schwab emits "NaN" for greeks on some illiquid contracts. One bad contract
        // must not take down the whole chain load.
        const string json = """
        {"symbol":"XYZ","underlyingPrice":50.0,
         "callExpDateMap":{"2026-09-18:23":{"55.0":[{
            "symbol":"XYZ   260918C00055000","putCall":"CALL","strikePrice":55.0,
            "bid":0.10,"ask":0.15,"mark":0.12,"totalVolume":0,"openInterest":3,
            "delta":"NaN","gamma":"NaN","theta":"NaN","vega":"NaN","volatility":"NaN",
            "intrinsicValue":0.0,"extrinsicValue":0.12,"multiplier":100.0,
            "daysToExpiration":23,"inTheMoney":false,"nonStandard":false}]}},
         "putExpDateMap":{}}
        """;
        var c = Assert.Single(OptionChainParser.Parse(json).Contracts);
        Assert.Equal(0m, c.Delta);
        Assert.Equal(0.10m, c.Bid);
    }

    // ── Quality columns ───────────────────────────────────────────

    [Fact]
    public void SpreadPct_IsMeasuredAgainstMid()
    {
        // bid 3.53 / ask 3.56 → mid 3.545, spread 0.03 → 0.846%
        Assert.Equal(0.85m, Math.Round(BySymbol(AtmCall).SpreadPct, 2));
    }

    [Fact]
    public void SpreadPct_OfAPennyContractIsTwoHundredPercent()
    {
        // bid 0.00 / ask 0.01 → mid 0.005, spread 0.01
        Assert.Equal(200m, BySymbol(PennyCall).SpreadPct);
    }

    [Fact]
    public void HasBid_IsFalseWhenNobodyIsBidding()
    {
        Assert.False(BySymbol(PennyCall).HasBid);
        Assert.True(BySymbol(AtmCall).HasBid);
    }

    [Fact]
    public void CommissionPctOfPremium_ExposesTheCostOfCheapContracts()
    {
        // $0.65 on a $0.01 ask = $1 of premium → 65%
        Assert.Equal(65m, BySymbol(PennyCall).CommissionPctOfPremium(0.65m));
        // the same commission on a $3.56 ask is negligible
        Assert.True(BySymbol(AtmCall).CommissionPctOfPremium(0.65m) < 1m);
    }

    // ── Liquidity gate ────────────────────────────────────────────

    [Fact]
    public void Gate_RejectsContractWithNoBidDespiteHighOpenInterest()
    {
        // This contract has open interest of 1,550 — an OI filter would admit it.
        var penny = BySymbol(PennyCall);
        Assert.True(penny.OpenInterest > 1_000);
        Assert.False(new LiquidityGate().Admits(penny));
    }

    [Fact]
    public void Gate_AdmitsTightlyQuotedContract()
        => Assert.True(new LiquidityGate().Admits(BySymbol(LiquidPut)));

    [Fact]
    public void Gate_RejectsSpreadWiderThanTheLimit()
        => Assert.False(new LiquidityGate(MaxSpreadPct: 0.5m).Admits(BySymbol(AtmCall)));

    [Fact]
    public void Gate_AppliesOpenInterestFloorWhenAsked()
    {
        // Deep-ITM 500 call is tightly quoted in relative terms but has zero OI.
        var itm = BySymbol("SPY   260828C00500000");
        Assert.Equal(0, itm.OpenInterest);
        Assert.False(new LiquidityGate(MinOpenInterest: 100).Admits(itm));
    }

    // ── Chain navigation ──────────────────────────────────────────

    [Fact]
    public void For_ReturnsOneRightOrderedByStrike()
    {
        var chain = Chain();
        var calls = chain.For(chain.Expirations.Single(), OptionRight.Call);
        Assert.Equal([500m, 767m, 810m], calls.Select(c => c.Strike));
    }

    // ── Expiry instant ─────────────────────────────────────────────

    [Fact]
    public void Parse_ReadsTheContractsOwnExpiryInstant()
    {
        // 2026-08-28T20:00:00Z is the 4pm ET close. Taking the instant from the broker
        // rather than assuming 4pm keeps index options and early closes correct.
        Assert.Equal(new DateTime(2026, 8, 28, 20, 0, 0, DateTimeKind.Utc),
                     BySymbol(AtmCall).ExpiresAtUtc);
    }

    [Fact]
    public void HasExpired_IsTrueOnceThatInstantHasPassed()
        => Assert.True(BySymbol(AtmCall).HasExpired);   // fixture is from August 2026

    [Fact]
    public void Parse_FallsBackToEndOfDayWhenTheBrokerOmitsTheInstant()
    {
        // Never earlier than the date itself — a missing field must not hide a live contract.
        const string json = """
        {"symbol":"XYZ","underlyingPrice":50.0,
         "callExpDateMap":{"2099-09-18:23":{"55.0":[{
            "symbol":"XYZ   990918C00055000","putCall":"CALL","strikePrice":55.0,
            "bid":0.10,"ask":0.15,"mark":0.12,"multiplier":100.0,
            "daysToExpiration":23,"inTheMoney":false,"nonStandard":false}]}},
         "putExpDateMap":{}}
        """;
        var c = Assert.Single(OptionChainParser.Parse(json).Contracts);
        Assert.Equal(new DateTime(2099, 9, 18), c.ExpiresAtUtc.Date);
        Assert.False(c.HasExpired);
    }
}
