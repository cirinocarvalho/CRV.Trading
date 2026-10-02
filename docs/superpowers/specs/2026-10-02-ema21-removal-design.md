# EMA21 Removal Design

## Overview

Retire the `Ema21Strategy` so its slot can be taken by the single EMA strategy (`ema-strategy`), and tighten the two config checks that strategy relies on: one bar size per root, and an invalid entry that disables only itself.

Part of the EMA set, built in this order: **ema21-removal** → hold-and-close → dollar-targets-and-rr-guard → htf-bars-and-history → ema-strategy → ema-parity-and-validation. `hold-and-close` and `dollar-targets-and-rr-guard` do not depend on this spec and can ship on their own.

Source: `docs/superpowers/2026-10-02-ema-strategy-kickoff.md` (spec 1, split in two during brainstorming).

## Strategy type numbers

`StrategyType` (`CRV.Core/Strategy/ISetupStrategy.cs:8`) is implicit today and basket JSON stores it as an int (no `JsonStringEnumConverter` anywhere). Make the numbers explicit:

```csharp
public enum StrategyType
{
    Pullback       = 0,
    Retest         = 1,
    OrbFakeout     = 2,
    SessionFakeout = 3,
    // 4 was Ema21; reserved so stored baskets and old backtest runs never reuse it.
    Ema            = 5,
}
```

Values 0–3 are unchanged, so no stored basket moves. `Ema = 5` is declared here so the number is fixed; the strategy itself arrives in `ema-strategy`, and until then `StrategyFactory` treats `Ema` and the reserved 4 like any unknown type (see [Invalid config](#invalid-config)).

The JS name array in `Pages/Setup/Strategy.cshtml:262` is indexed by these numbers and becomes a lookup keyed by number, with no entry for 4.

## Removal

Delete:

- `CRV.Core/Strategy/Ema21Strategy.cs`
- `CRV.Core.Tests/Strategy/Ema21StrategyTests.cs`

Remove from `StrategySetupConfig` (`:72-84`): `SlopeLen`, `AtrTouchMult`, `MinSlopePct`, `OpenTicksToEma`, `UseVolumeFilter`. Keep `AtrTp1Mult` / `AtrTp2Mult` (the EMA strategy's ATR targets use them).

Update every reference:

| Location | Change |
|---|---|
| `StrategyFactory.cs:17` | Drop the `Ema21` case |
| `StrategyConfig.cs:355` | `Ema21BasketJson` → `EmaBasketJson` |
| `StrategyConfig.cs:561, 605-621` | `ToEma21SetupConfigs` → `ToEmaSetupConfigs`; stop swallowing malformed JSON (see below) |
| `StrategyConfig.cs:627, 694, 704-720` | `TfMinutesFor`, `MaxTfMinutes`, `EnumerateBasketEntries` read `EmaBasketJson` |
| `StrategyConfig.cs:776-783` | Drop the EMA21-only field mapping |
| `StrategyBasketService.cs:7, 30-31, 86, 113-126` | `IsEma21` → `IsEmaBasket`; route `StrategyType.Ema` to the EMA basket |
| `StrategyConfigService.cs:51` | `EmaBasketJson ??= ""` |
| `Program.cs:254` | NULL repair on `EmaBasketJson` |
| `LiveEngineOrchestrator.cs:1464-1465` | Auto-roll rewrites `EmaBasketJson` |
| `TickerAutoRoller.cs:11` | Doc comment only |
| `Pages/Setup/Strategy.cshtml(.cs)` | Remove the EMA21 badge (`:46`), read-only type (`:74-78`), EMA21 fields (`:161-165`), `IsEma21` (`.cs:28, 126`) |
| `Pages/Setup/Strategies.cshtml(.cs)` | Drop Ema21 from the add picker (`:73`); `LegacyInUse` (`.cs:37`) uses `IsEmaBasket` |
| `Pages/Setup/StrategyText.cs:15, 37` | Drop the Ema21 text |
| `SnapshotAggregator.cs:264`, `Prospectus.cshtml.cs:168, 252, 277` | Read the type name; no Ema21 branch left |

**Keep:** `Ema21Indicator` (`Indicators.cs:46`), `IndicatorState.Ema21`, the dashboard EMA21 overlay, and `UseEmaFilter` with its uses in `TickerGroup`, the strategies and `ValidationRunner`.

Mark `docs/superpowers/specs/2026-04-07-ema21-strategy-design.md` and `docs/superpowers/plans/2026-04-07-ema21-strategy.md` as superseded with a one-line header pointing at this spec.

## Migration

One EF migration (`RenameEmaBasketColumn`) renames column `Configs.Ema21BasketJson` → `EmaBasketJson`.

Stored EMA21 entries (`"StrategyType":4`) stay in `EmaBasketJson` as they are. Type 4 is reserved and has no factory case, so engine start reports each one as "Disabled: retired EMA21 strategy" through the [invalid-config](#invalid-config) path and the rest of the basket keeps trading. The setup page shows them read-only with the same message. Their conversion into disabled `Ema` entries (Source = PriceVsEma, Entry = Touch, EMA 21, same bar size, " (migrated from EMA21)" on the label) belongs to the `ema-strategy` migration, because the fields it writes are added by that spec; writing them earlier would lose them on the next basket save.

Historical `TradeRecord`s are not touched (they only carry `SetupLabel` strings).

**Old backtest runs** (`BacktestRuns.ResultJson`) store the whole `StrategyConfig` with its baskets as plain strings. They already load without parsing the baskets (`Results.cshtml.cs:194-206`). The one path that could reach the factory with type 4 is anything that rebuilds setups from a loaded run; it must treat such a run as read-only legacy and not crash.

## Bar size per root

Strategies on the same **root** (one `TickerGroup`: NQ and MNQ, ES and MES, …, merged at `TickerGroup.cs:826-837`) must share one `ExecutionTFMinutes`.

- **Validation** (`StrategyConfig.Validate()` and web save) rejects enabled entries on one root with different bar sizes, across both the ORB basket and the EMA basket. The error names the entries and their bar sizes.
- **Feed:** `TfMinutesFor` resolves by root, ignoring disabled entries (today it takes the first exact-ticker match, disabled included).
- **Engine group:** the group's ORB calculator and modules use the root's bar size, not the global `ExecutionTFMinutes` (`TickerGroup.cs:89, 108, 134`).
- **Label:** the Bar size field reads "Shared by every NQ / MNQ strategy" (the root's micro and mini symbols).

## Invalid config

- **Web save** is blocked for an invalid entry. `StrategyConfig.Validate()` covers the EMA basket too (it reads only `BasketJson` today), and its findings for the entry being saved become blocking, not warnings.
- **Engine start:** each entry is validated as it is added (`LiveEngineOrchestrator` `AddSetup` path, `:689-694`). An invalid entry, including an unknown `StrategyType`, is logged, skipped and reported as disabled; the rest of the basket keeps trading. Today an unknown type makes `StrategyFactory` throw and the whole engine stops with `Error: …`.
- **Malformed basket JSON** is an error shown on the Strategies page and in the log, not a silent fallback to setups A–D (`StrategyConfig.cs:553`) or a silently dropped EMA basket (`:619`).
- **Visible state:** the cockpit card and the setup page show "Disabled: &lt;reason&gt;", as on the signal-logic canvas: a DISABLED pill, the reason in words, and a "Fix in Setup" link.

## File Structure

### Deleted Files

| File |
|---|
| `CRV.Core/Strategy/Ema21Strategy.cs` |
| `CRV.Core.Tests/Strategy/Ema21StrategyTests.cs` |

### New Files

| File | Responsibility |
|---|---|
| `CRV.Core/Migrations/<ts>_RenameEmaBasketColumn.cs` | Column rename |
| `CRV.Core/Models/SetupValidation.cs` | Per-entry and per-root validation shared by web save and engine start |

### Modified Files

As listed in [Removal](#removal), plus `TickerGroup.cs` (root bar size), `LiveEngineOrchestrator.cs` (skip invalid entries), `Pages/Dashboard/Index.cshtml` (disabled card), and the two EMA21 superpowers docs.

## Tests

1. A full-repo search finds no `Ema21Strategy`, `StrategyType.Ema21`, `Ema21BasketJson`, `ToEma21SetupConfigs`, `IsEma21` or the five removed fields outside `CRV.Core/Migrations/`. (`Ema21Indicator`, `IndicatorState.Ema21` and `UseEmaFilter` stay.)
2. `StrategyType` numbers: Pullback 0, Retest 1, OrbFakeout 2, SessionFakeout 3, Ema 5; a basket with 0–3 round-trips unchanged.
3. Migration: the column is renamed with its content intact, other columns untouched; a stored type-4 entry is reported "Disabled: retired EMA21 strategy" at engine start and on the setup page, and the basket's other entries trade.
4. A saved backtest run whose config holds a type-4 entry loads and renders read-only.
5. Bar size per root: MNQ at 5 min with NQ at 15 min is rejected on save; the same with one entry disabled is accepted; ORB basket vs EMA basket mismatch is rejected; the feed and the group ORB use the root's bar size.
6. Engine start with one invalid entry (unknown type, mismatched bar size): that entry is skipped and reported "Disabled: …"; the others arm and trade.
7. Malformed basket JSON is reported as an error, not replaced by setups A–D.
