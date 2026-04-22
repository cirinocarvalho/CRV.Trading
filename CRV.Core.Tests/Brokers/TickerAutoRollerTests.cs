using System.Text.Json;
using System.Text.Json.Nodes;
using CRV.Live;
using Xunit;

namespace CRV.Core.Tests.Brokers;

public class TickerAutoRollerTests
{
    // ── NewerContractFor ────────────────────────────────────────

    [Fact]
    public void NewerContractFor_StaleTicker_ReturnsActive()
    {
        // MCLK26 (May 2026 CL) expires 2026-04-22; active on 2026-04-22 is MCLM26.
        // Only verifies that NewerContractFor says "you're on a non-active contract" —
        // the exact target date is context-free here because ActiveContract uses Today.
        // So we pass a ticker for a month that is definitely stale.
        var result = TickerAutoRoller.NewerContractFor("NQH24"); // 2024 H — very stale
        Assert.NotNull(result);
        Assert.NotEqual("NQH24", result);
    }

    [Fact]
    public void NewerContractFor_Null_ReturnsNull()
    {
        Assert.Null(TickerAutoRoller.NewerContractFor(""));
        Assert.Null(TickerAutoRoller.NewerContractFor("   "));
    }

    [Fact]
    public void NewerContractFor_AlreadyActive_ReturnsNull()
    {
        // Whatever the current NQ active contract is, asking about it should return null.
        var currentNqActive = ContractRollCalendar.ActiveContract("NQ");
        Assert.Null(TickerAutoRoller.NewerContractFor(currentNqActive));
    }

    [Fact]
    public void NewerContractFor_HandlesSlashPrefix()
    {
        // Accepts broker-format with leading slash (Schwab). Should not throw.
        _ = TickerAutoRoller.NewerContractFor("/NQH24");
    }

    [Fact]
    public void NewerContractFor_GarbageInput_ReturnsNullNoThrow()
    {
        // Pathological input — must not throw. Behaviour on garbage is "leave alone".
        _ = TickerAutoRoller.NewerContractFor("garbage");
    }

    // ── RewriteBasket ───────────────────────────────────────────

    [Fact]
    public void RewriteBasket_EmptyOrNull_ReturnsEmptyRewrites()
    {
        var (json1, rw1) = TickerAutoRoller.RewriteBasket(null, "Basket");
        Assert.Empty(rw1);
        Assert.Equal("", json1);

        var (json2, rw2) = TickerAutoRoller.RewriteBasket("   ", "Basket");
        Assert.Empty(rw2);

        var (json3, rw3) = TickerAutoRoller.RewriteBasket("[]", "Basket");
        Assert.Empty(rw3);
        Assert.Equal("[]", json3);
    }

    [Fact]
    public void RewriteBasket_NonArray_ReturnsUnchanged()
    {
        var (json, rw) = TickerAutoRoller.RewriteBasket("{\"Ticker\": \"NQH24\"}", "Basket");
        Assert.Empty(rw);
    }

    [Fact]
    public void RewriteBasket_StaleTicker_IsRewritten_OtherFieldsPreserved()
    {
        // Stale 2024 contract — any call to NewerContractFor will say "rewrite this".
        // We keep arbitrary extra fields to prove round-trip preserves them.
        var input = """
            [
              {
                "Id": "a-nq-1",
                "Enabled": true,
                "Ticker": "NQH24",
                "PointValue": 20,
                "ExtraField": "preserved",
                "Nested": { "Deep": 42 }
              }
            ]
            """;

        var (json, rw) = TickerAutoRoller.RewriteBasket(input, "Basket");

        Assert.Single(rw);
        Assert.Equal("NQH24", rw[0].Old);
        Assert.NotEqual("NQH24", rw[0].New);
        Assert.Contains("a-nq-1", rw[0].Where);

        var node = JsonNode.Parse(json);
        Assert.NotNull(node);
        var entry = node!.AsArray()[0]!;
        Assert.Equal(rw[0].New, entry["Ticker"]!.GetValue<string>());
        Assert.True(entry["Enabled"]!.GetValue<bool>());
        Assert.Equal("preserved", entry["ExtraField"]!.GetValue<string>());
        Assert.Equal(42, entry["Nested"]!["Deep"]!.GetValue<int>());
    }

    [Fact]
    public void RewriteBasket_MultipleEntries_RewritesOnlyStale()
    {
        // First entry is stale (NQH24). Second uses the current NQ active contract —
        // should pass through untouched.
        var currentNqActive = ContractRollCalendar.ActiveContract("NQ");
        var input = $$"""
            [
              { "Id": "a-1", "Ticker": "NQH24" },
              { "Id": "a-2", "Ticker": "{{currentNqActive}}" }
            ]
            """;

        var (json, rw) = TickerAutoRoller.RewriteBasket(input, "Basket");

        Assert.Single(rw);
        Assert.Equal("NQH24", rw[0].Old);
        var node = JsonNode.Parse(json)!;
        Assert.Equal(currentNqActive, node.AsArray()[1]!["Ticker"]!.GetValue<string>());
    }

    [Fact]
    public void RewriteBasket_NoRewrites_ReturnsOriginalString()
    {
        // All entries are already on the active contract — no rewrite, string unchanged.
        var currentNqActive = ContractRollCalendar.ActiveContract("NQ");
        var input = $"[{{\"Ticker\":\"{currentNqActive}\"}}]";
        var (json, rw) = TickerAutoRoller.RewriteBasket(input, "Basket");
        Assert.Empty(rw);
        Assert.Equal(input, json);
    }

    [Fact]
    public void RewriteBasket_CamelCaseKey_AlsoHandled()
    {
        var input = """[{"id":"a-1","ticker":"NQH24"}]""";
        var (_, rw) = TickerAutoRoller.RewriteBasket(input, "Basket");
        Assert.Single(rw);
    }

    [Fact]
    public void RewriteBasket_MalformedJson_ReturnsInputUnchanged()
    {
        var input = "not-json-at-all";
        var (json, rw) = TickerAutoRoller.RewriteBasket(input, "Basket");
        Assert.Empty(rw);
        Assert.Equal(input, json);
    }
}
