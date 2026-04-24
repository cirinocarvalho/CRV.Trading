using CRV.Core.Data;
using CRV.Core.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace CRV.Web.Api;

[ApiController]
[Route("api/admin")]
[EnableRateLimiting("engine-api")]
public class AdminController : ControllerBase
{
    private readonly TradingDbContext _db;
    private readonly ILogger<AdminController> _log;

    public AdminController(TradingDbContext db, ILogger<AdminController> log)
    {
        _db = db;
        _log = log;
    }

    // ── Cleanup: phantom SessionEnd trades ─────────────────────────
    //
    // Deletes TradeRecord rows whose ExitReason is SessionEnd within an
    // optional date range and optional setup filter. These accumulate when
    // the recovery path used to re-register externally-flattened trades on
    // engine start (fixed in PR #8 but pre-existing rows need manual cleanup).

    /// <summary>
    /// Preview or delete phantom SessionEnd trade records.
    /// GET = dry-run (returns count + rows). POST with ?confirm=true = delete.
    /// </summary>
    [HttpGet("cleanup/phantom-trades")]
    public async Task<IActionResult> PreviewPhantomTrades(
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] string? setup = null)
    {
        var q = BuildPhantomQuery(from, to, setup);
        var rows = await q
            .OrderByDescending(t => t.EnteredAt)
            .Select(t => new { t.Id, t.Setup, t.Ticker, t.EnteredAt, t.ExitedAt, t.Exit, t.ExitReason, t.NetPnl })
            .ToListAsync();
        return Ok(new { count = rows.Count, rows });
    }

    [HttpPost("cleanup/phantom-trades")]
    public async Task<IActionResult> DeletePhantomTrades(
        [FromQuery] bool confirm = false,
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] string? setup = null)
    {
        if (!confirm)
            return BadRequest(new { status = "error", message = "Pass ?confirm=true to actually delete. Without it this endpoint only accepts GET (dry-run)." });

        var q = BuildPhantomQuery(from, to, setup);
        var deleted = await q.ExecuteDeleteAsync();
        _log.LogWarning("[ADMIN] Deleted {N} phantom SessionEnd trades (from={From}, to={To}, setup={Setup})",
            deleted, from, to, setup);
        return Ok(new { status = "ok", deleted });
    }

    private IQueryable<TradeRecord> BuildPhantomQuery(DateTime? from, DateTime? to, string? setup)
    {
        var q = _db.Trades.AsQueryable()
            .Where(t => t.ExitReason == ExitReason.SessionEnd);
        if (from.HasValue) q = q.Where(t => t.EnteredAt >= from.Value);
        if (to.HasValue)   q = q.Where(t => t.EnteredAt <  to.Value);
        // Setup filter matches the free-text label (e.g. "retest-mnq") since
        // `Setup` on TradeRecord is the SetupId enum (A/B/C/D/F), not the label.
        if (!string.IsNullOrWhiteSpace(setup))
            q = q.Where(t => t.SetupLabel == setup);
        return q;
    }

    // ── Cleanup: backtest runs table ───────────────────────────────
    //
    // Truncates or date-filters the BacktestRuns table. Backtests accumulate
    // quickly in dev and are only kept for historical reference.

    /// <summary>
    /// Preview or delete BacktestRun rows. GET = dry-run, POST with ?confirm=true = delete.
    /// </summary>
    [HttpGet("cleanup/backtest-runs")]
    public async Task<IActionResult> PreviewBacktestRuns(
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] string? ticker = null)
    {
        var q = BuildBacktestQuery(from, to, ticker);
        var count = await q.CountAsync();
        var sample = await q
            .OrderByDescending(r => r.RunAt)
            .Take(25)
            .Select(r => new { r.Id, r.Ticker, r.ConfigName, r.From, r.To, r.RunAt, r.TotalTrades, r.NetPnl })
            .ToListAsync();
        return Ok(new { count, sample });
    }

    [HttpPost("cleanup/backtest-runs")]
    public async Task<IActionResult> DeleteBacktestRuns(
        [FromQuery] bool confirm = false,
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] string? ticker = null)
    {
        if (!confirm)
            return BadRequest(new { status = "error", message = "Pass ?confirm=true to actually delete. Without it this endpoint only accepts GET (dry-run)." });

        var q = BuildBacktestQuery(from, to, ticker);
        var deleted = await q.ExecuteDeleteAsync();
        _log.LogWarning("[ADMIN] Deleted {N} backtest runs (from={From}, to={To}, ticker={Ticker})",
            deleted, from, to, ticker);
        return Ok(new { status = "ok", deleted });
    }

    private IQueryable<BacktestRunRow> BuildBacktestQuery(DateTime? from, DateTime? to, string? ticker)
    {
        var q = _db.BacktestRuns.AsQueryable();
        if (from.HasValue) q = q.Where(r => r.RunAt >= from.Value);
        if (to.HasValue)   q = q.Where(r => r.RunAt <  to.Value);
        if (!string.IsNullOrWhiteSpace(ticker))
            q = q.Where(r => r.Ticker == ticker);
        return q;
    }
}
