# WCAG 2.2 Accessibility Gate Design

## Overview

Add an automated accessibility test that scans every CRV.Web page with axe-core against the WCAG 2.2 A and AA rules, run it in CI so it gates PRs and deploys, and fix every violation it finds.

The gate covers what axe can check automatically. It cannot prove full WCAG 2.2 conformance: criteria that need human judgement are listed in the README (see [Manual criteria](#manual-criteria)) rather than tested.

## Goals

- `dotnet test CRV.Web.A11yTests` fails on any axe violation tagged `wcag2a`, `wcag2aa`, `wcag21a`, `wcag21aa` or `wcag22aa`.
- Every real page is scanned in both themes (dark, light) at desktop (1440 px) and phone (390 px) widths, plus the modal states that every page shares.
- CI runs the scan on every push and PR to `master`; deploys wait for it.
- The current violations are fixed, so the gate lands green.

## Non-goals

- No change to trading behaviour, order handling, or any Core/Live/Backtest code.
- No Node toolchain.
- No baseline or allowlist of known violations.
- No data-table alternatives for charts.
- No automated tests for the manual criteria.

## Architecture

### Test project

New project `CRV.Web.A11yTests` (net10.0, xUnit 2.9.3, same runner and SDK packages as `CRV.Core.Tests`), added to `CRV.Trading.sln`.

| Package | Purpose |
|---|---|
| `Microsoft.AspNetCore.Mvc.Testing` | Hosts CRV.Web in the test process (`WebApplicationFactory<Program>`) |
| `Microsoft.Playwright` | Drives headless Chromium |
| `Deque.AxeCore.Playwright` | Runs axe-core in the page and returns results |

CRV.Web gains one line so the factory can reach its top-level `Program`:

```csharp
public partial class Program;
```

That is the only C# change outside the test project.

### Hosting: `A11yAppFixture`

An xUnit collection fixture shared by every test in the project.

- Builds a `WebApplicationFactory<Program>` that serves over real Kestrel on `http://127.0.0.1:<free port>` (`UseKestrel()`, new in .NET 10), so Playwright can reach it. The plan's first task verifies this works with the top-level `Program`; if not, the fallback is to start Kestrel through `IWebHostBuilder.UseUrls` in `ConfigureWebHost`.
- Environment `Production`. Development is ruled out: it binds HTTPS with a dev cert and sets `Options:AllowLiveOrders: true`.
- `DATA_DIR` points at a fresh temp folder per run, so the SQLite DB, token files and `live_settings.json` start empty. `Program.cs` changes the working directory to `DATA_DIR`; the fixture sets the variable before the host builds and restores the original directory on dispose.
- Configuration overrides through `ConfigureAppConfiguration`:
  - `Options:SnapshotSymbols` empty, so `OptionChainSnapshotService` never calls Schwab.
  - `Seq:Url` empty, to stop the sink retrying `localhost:5341`.
- Launches one headless Chromium for the whole run and disposes it with the host.

### Seed data: `A11ySeed`

Seeds the DB through the app's own `TradingDbContext` and `StrategyConfigService` after startup migrations have run, so pages render real content instead of empty states:

- Config row `Id=1` with `Broker = "Mock"`, so broker-backed pages render the mock path instead of Schwab error states.
- One basket entry of each strategy type (ORB basket and EMA21 basket). The first entry's id drives the `/setup/strategies/{id}` scan.
- A handful of closed trades for `live` and `paper`, spread over two trading days, so Sessions, Results and Prospectus render tables and charts.
- One saved backtest run, so `/review/results?source=backtest` renders.

Seed values are fixed literals. No credentials, tokens or account numbers.

### Pages: `A11yPages`

The scan list. Every real page is included; OAuth callbacks (`/auth/schwab`, `/auth/tradestation`) and legacy redirects are excluded.

| Route | Notes |
|---|---|
| `/dashboard` | Cockpit |
| `/dashboard/prospectus` | |
| `/dashboard/sessions` | |
| `/review/results?source=live` | |
| `/review/results?source=backtest` | |
| `/validation` | |
| `/trading/positions` | |
| `/trading/orders` | |
| `/options/explorer` | |
| `/options/positions` | |
| `/setup/strategies` | |
| `/setup/strategies/{id}` | First seeded entry |
| `/setup/brokers` | |
| `/setup/risk` | |
| `/setup/alerts` | |

Each entry can carry an interaction to run before the scan, for UI that is hidden on load. Interactions shipped:

- Flatten confirmation modal (shared layout), scanned on `/dashboard`.
- Order ticket (`_OrderTicket`), opened and scanned on `/dashboard`.
- Legacy setups expanded (`_LegacySetups`), scanned on `/setup/strategies`.

### Tests: `PageScanTests`

A `[Theory]` over page × theme × viewport:

- Theme: `dark`, `light`. Set by writing the `crv-theme` key to `localStorage` through an init script before navigation, the same key `crv-ui.js` reads.
- Viewport: 1440 × 900 and 390 × 844.
- Waits for `load` and for network idle, then runs axe with `WithTags("wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa")`.
- Fails when `Violations` is non-empty. The failure message lists, per violation: rule id, impact, help URL, and each node's selector and HTML snippet.
- On failure, saves a full-page screenshot to `TestResults/a11y/<page>-<theme>-<width>.png`.
- `Incomplete` results (needs review) are written to the test output but do not fail the test.

15 pages × 2 themes × 2 viewports = 60 scans, plus the 3 interaction scans per theme and viewport.

**Rule exceptions:** none planned. If a rule produces a confirmed false positive, it is disabled for that one page with `DisableRules("<rule-id>")` and a comment explaining why, and listed in the PR description.

## CI

New job `a11y` in `.github/workflows/ci.yml`, running in parallel with the existing `build-and-test` job:

1. `actions/checkout`, `actions/setup-dotnet` with `global-json-file: global.json`.
2. `dotnet restore` and `dotnet build -c Release` of the solution.
3. Cache `~/.cache/ms-playwright`, keyed on the `Microsoft.Playwright` package version.
4. `pwsh CRV.Web.A11yTests/bin/Release/net10.0/playwright.ps1 install --with-deps chromium`.
5. `dotnet test CRV.Web.A11yTests --no-build -c Release --logger "trx;LogFileName=a11y-results.trx"`.
6. On failure: upload the trx and `TestResults/a11y/*.png` as the `a11y-results` artifact.

The existing test step stays scoped to `CRV.Core.Tests`. `deploy.yml` reuses `ci.yml` through `workflow_call`, and its deploy job is updated to need both jobs.

Pages load Bootstrap, SignalR, Chart.js and lightweight-charts from jsDelivr and unpkg; the runner needs outbound network. A CDN outage fails the run; no retry logic is added.

## Fixing violations

1. Land the harness first and commit it with the scans failing, so the first run records the full list of violations.
2. Fix shared code first: `_Layout`, `_EngineBar`, `_OrderTicket`, `_ResultsView`, `_SetupConfigSection`, `_LegacySetups`, `tokens.css`, `shell.css`, `components.css`, `crv.css`. One fix there clears the violation on every page.
3. Then fix page by page.
4. Commit per rule category (contrast, names and labels, ARIA, target size, …).

Constraints on fixes:

- Markup attributes, labels, text alternatives and styles only. No trading logic changes.
- **Contrast:** token changes are made in both themes. The PR description carries a table of every changed token: name, theme, old value, new value, contrast ratio against its background.
- **Charts:** each chart container gets an accessible name (`role="img"` and an `aria-label` naming the instrument and bar size, e.g. "MNQ price chart, 5-minute bars").
- A fix that needs more than a style change (moving or restructuring markup on a screen) stops for an HTML mockup and approval first.

## Manual criteria

A short "Accessibility" section is added to `README.md`. It states that the CI gate covers the axe-checkable WCAG 2.2 A/AA rules, how to run it locally, and that these criteria need a manual check:

- 2.1.2 No Keyboard Trap
- 2.4.3 Focus Order
- 2.4.7 Focus Visible
- 2.4.11 Focus Not Obscured (Minimum), relevant because of the sticky engine bar
- 2.5.7 Dragging Movements
- 3.3.7 Redundant Entry
- 3.3.8 Accessible Authentication (Minimum)

## File Structure

### New Files

| File | Responsibility |
|---|---|
| `CRV.Web.A11yTests/CRV.Web.A11yTests.csproj` | Test project |
| `CRV.Web.A11yTests/A11yAppFixture.cs` | Hosts CRV.Web on Kestrel, owns Chromium |
| `CRV.Web.A11yTests/A11ySeed.cs` | Seeds config, basket entries, trades, backtest run |
| `CRV.Web.A11yTests/A11yPages.cs` | Page list and interactions |
| `CRV.Web.A11yTests/PageScanTests.cs` | The axe scan theory |
| `CRV.Web.A11yTests/AxeReport.cs` | Formats violations into the failure message |

### Modified Files

| File | Change |
|---|---|
| `CRV.Web/Program.cs` | `public partial class Program;` |
| `CRV.Trading.sln` | Adds the test project |
| `.github/workflows/ci.yml` | New `a11y` job |
| `.github/workflows/deploy.yml` | Deploy needs both CI jobs |
| `README.md` | Accessibility section |
| `CRV.Web/Pages/**`, `CRV.Web/wwwroot/css/**`, `CRV.Web/wwwroot/js/**` | Violation fixes |

## Tests

1. The fixture boots CRV.Web in Production with an empty `DATA_DIR` and serves `/dashboard` with HTTP 200.
2. The seeded `/setup/strategies/{id}` returns 200, not 404.
3. Every page × theme × viewport scan has zero WCAG A/AA violations.
4. Each interaction scan (flatten modal, order ticket, legacy setups) has zero violations.
5. A page loaded with `SetContentAsync("<img src='x.png'>")` (no `alt`) produces an `image-alt` violation through the same scan and report code. This proves the gate can go red.
