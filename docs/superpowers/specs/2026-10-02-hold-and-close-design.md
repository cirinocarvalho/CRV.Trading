# Hold and Close Design

## Overview

Make `CloseAtRthClose` do what its label says. Today it is stored and mapped but nothing reads it: every setup is flattened at its cutoff and again at session end. With this spec a strategy either closes (today's behaviour, and the default) or holds an open position into the next session, and a held position's open loss counts against the daily loss limit.

Part of the EMA set (order: ema21-removal → **hold-and-close** → dollar-targets-and-rr-guard → htf-bars-and-history → ema-strategy → ema-parity-and-validation). It does not depend on the other specs and can ship on its own.

Source: kickoff spec 1, "Make CloseAtRthClose work", split out during brainstorming.

## Where positions are closed today

Two separate exits, both unconditional:

| Exit | Where | What it does |
|---|---|---|
| **Setup cutoff** | `TickerGroup.ProcessBarAsync` `:289-309`, `ProcessTickAsync` `:403-413` | Past the setup's cutoff (`IsPastCutoff`, `:884-898`, from the session slot or `CutoffHour/Minute`): an active setup gets `ForceExit` + `ExitGroupAsync(SessionEnd)`; a pending entry is cancelled; then `Disarm()`. The session-disabled blocks (`:275-285`, `:392-401`) exit the same way. |
| **Session end** | `BacktestEngine.cs:223-243` (`ForceExitAllAsync`, `:240`), `LiveEngineOrchestrator.cs:1072-1073` | `ComposableEngine.ForceExitAllAsync` (`:402-409`) closes every active group (`BrokerEventHandler.ExitAllAsync`, `:580-591`) and calls `ResetSession()` on every strategy. Skipped during warmup. |

`CloseAtRthClose` appears in config, mapping and UI (`Strategy.cshtml:188`, "Close at the end of the session") but is read only in a comment (`TickerGroup.cs:291`).

## Behaviour

`ISetupStrategy` gains `bool CloseAtRthClose { get; }`, implemented by every strategy from its config (`ManualStrategy` returns `true`).

| | CloseAtRthClose = **true** (default) | CloseAtRthClose = **false** |
|---|---|---|
| At the setup cutoff | Close the open position, cancel a pending entry, disarm (today) | Cancel a pending entry, disarm, block new entries; **keep the open position** |
| At session end | Close (today) | `ForceExitAllAsync` skips this strategy's group; the position carries into the next session |
| Session reset | — | `ResetSession()` / `Reset()` leave `InTrade` set and keep the group tracked |
| Next session | — | Exits (stop, targets, trail, manual Exit now) keep working; no new entry until the position closes |

The setup-not-enabled-for-this-session exit (`:275-285`, `:392-401`) follows the same rule: a holding strategy keeps its position when its session slot is off.

**`InTrade` on reset.** Only `Ema21Strategy` clears `_inTrade` in `Reset` / `ResetSession` today (it is removed by `ema21-removal`). The rule becomes explicit and tested for every strategy: a reset never clears `InTrade`; only the broker event handler's completion does (`SetInTrade(false)` in `CompleteGroup`).

**Defaults.** The migration sets `CloseAtRthClose = true` on every existing basket entry (both baskets) and every legacy setup A–D, so nothing changes until the user unticks it. New strategies of every type default to `true`; holding is an explicit choice. (The EMA mockup shows it off by default; the default is on, because an unintended overnight hold costs more than an unintended close.)

## Daily loss with held positions

Today the daily limit counts realized P&L only: `RiskManager.TodayPnl` moves in `RecordTrade` (`:49-70`), `DdBreached` (`:31-35`) compares it to the limit, and the gates are `ComposableEngine.cs:152` and `:194`. `BrokerEventHandler.GetUnrealizedPnl` (`:596`) feeds only the snapshot. `DailyStatsService` (`:82-123`) is also realized-only and rolls its day on the **UTC** date.

New rule:

- The daily-loss check uses **realized P&L today + unrealized P&L of held positions**, marked to the last price. A held position opened on an earlier trading day counts in full; one opened today already counts through its normal open P&L.
- Realized P&L still books on the trading day the trade closes.
- When the check trips, new entries stop as today; held positions are not force-closed by it (their own stops still apply).
- `DailyStatsService` rolls on the trading date (`StrategyConfig.TradingDate()`), the same day the engine uses, not the UTC date.

## UI

- Setup page: the existing "Close at the end of the session" switch, help text "Off: the cutoff only stops new entries, and an open trade is held into the next session."
- Cockpit card footer: "closes at session end" or "holds past cutoff" (as in the signal-logic canvas).
- `StrategyText`: "Closes at the end of the session." / "Holds an open trade past the cutoff."

## File Structure

### New Files

| File | Responsibility |
|---|---|
| `CRV.Core/Migrations/<ts>_DefaultCloseAtRthClose.cs` | Sets `CloseAtRthClose = true` on every stored basket entry and legacy setup |

### Modified Files

| File | Change |
|---|---|
| `CRV.Core/Strategy/ISetupStrategy.cs` | `CloseAtRthClose` member |
| `CRV.Core/Strategy/*Strategy.cs` | Implement it; resets keep `InTrade` |
| `CRV.Core/Strategy/TickerGroup.cs` | Cutoff and session-disabled exits respect it |
| `CRV.Core/Strategy/ComposableEngine.cs` | `ForceExitAllAsync` skips holding groups; daily-loss gate uses realized + held unrealized |
| `CRV.Core/Strategy/BrokerEventHandler.cs` | `ExitAllAsync` takes the set of groups to close |
| `CRV.Core/Strategy/RiskManager.cs` | Breach check with an unrealized input |
| `CRV.Backtest/Engine/BacktestEngine.cs` | Session-end exit respects holding strategies |
| `CRV.Web/Services/LiveEngineOrchestrator.cs` | Same for live |
| `CRV.Web/Services/DailyStatsService.cs` | Trading-date rollover; held unrealized in the limit |
| `CRV.Web/Pages/Setup/Strategy.cshtml`, `StrategyText.cs`, `Pages/Dashboard/Index.cshtml` | Help text, plain words, card footer |

## Tests

1. Hold-vs-close matrix, live path (`TickerGroup` + `ComposableEngine` with the mock executor) and backtest path (`BacktestEngine`):
   - true: closed at cutoff; closed at session end.
   - false: kept at cutoff with no new entry; kept at session end; still exits on its stop the next session.
2. Session-disabled slot: a holding strategy keeps its position; a closing one is flattened.
3. `Reset()` and `ResetSession()` keep `InTrade` for every strategy type; `CompleteGroup` clears it.
4. Daily loss: a held position opened yesterday with an open loss past the limit stops new entries today; realized P&L lands on the close day; a held winner does not hide today's realized loss.
5. `DailyStatsService` rolls at the trading date, not UTC midnight.
6. Migration: every basket entry and legacy setup has `CloseAtRthClose = true` afterwards; other fields untouched.
