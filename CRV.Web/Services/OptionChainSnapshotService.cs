using CRV.Core.Data;
using CRV.Core.Models;
using CRV.Core.Options;
using CRV.Live.Brokers.Schwab;
using Microsoft.EntityFrameworkCore;

namespace CRV.Web.Services;

/// <summary>
/// Records one implied-volatility reading per underlying and expiry per session.
/// <para>Implied volatility history cannot be bought back. The chain reports what IV is
/// now and nothing about what it has been, so "is this expensive?" stays unanswerable
/// until enough readings accumulate. Starting today costs a few kilobytes a day; starting
/// in six months costs six months.</para>
/// <para>Deliberately small: per expiry it keeps the at-the-money IV and straddle, not the
/// chain. Full chains would be the only way to backtest structures later, but at ~4 MB per
/// symbol per capture that is a different decision with a real storage cost — this one is
/// cheap enough to need no justification.</para>
/// </summary>
public class OptionChainSnapshotService : BackgroundService
{
    private readonly SchwabAuthService  _schwab;
    private readonly IHttpClientFactory _httpFactory;
    private readonly IConfiguration     _config;
    private readonly IServiceProvider   _services;
    private readonly ILogger<OptionChainSnapshotService> _log;

    private static readonly TimeZoneInfo Eastern =
        TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "Eastern Standard Time" : "America/New_York");

    public OptionChainSnapshotService(
        SchwabAuthService schwab, IHttpClientFactory httpFactory, IConfiguration config,
        IServiceProvider services, ILogger<OptionChainSnapshotService> log)
    {
        _schwab = schwab; _httpFactory = httpFactory; _config = config;
        _services = services; _log = log;
    }

    private string[] Watchlist =>
        _config.GetSection("Options:SnapshotSymbols").Get<string[]>() ?? [];

    /// <summary>Eastern-time hour to capture. Late enough to be a settled reading, before the close.</summary>
    private int CaptureHour => _config.GetValue("Options:SnapshotHourEastern", 15);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (Watchlist.Length == 0)
        {
            _log.LogInformation("Option chain snapshots disabled — no Options:SnapshotSymbols configured");
            return;
        }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var nowEt = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Eastern);

                // Weekdays only, at or after the capture hour, and only if today is missing.
                if (nowEt.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) &&
                    nowEt.Hour >= CaptureHour)
                {
                    await CaptureAsync(DateOnly.FromDateTime(nowEt.Date), ct);
                }
            }
            catch (Exception ex)
            {
                // Never let a capture failure stop the loop — tomorrow's reading still matters.
                _log.LogWarning(ex, "Option chain snapshot pass failed");
            }

            try { await Task.Delay(TimeSpan.FromMinutes(30), ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// Capture now rather than waiting for the schedule. Public so a capture can be proven
    /// to work on the day it is written instead of the morning after.
    /// </summary>
    public async Task<int> CaptureNowAsync(CancellationToken ct = default)
    {
        var nowEt = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Eastern);
        return await CaptureAsync(DateOnly.FromDateTime(nowEt.Date), ct);
    }

    private async Task<int> CaptureAsync(DateOnly tradeDate, CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        int total = 0;

        foreach (var symbol in Watchlist)
        {
            if (ct.IsCancellationRequested) return total;

            bool already = await db.OptionChainSnapshots
                .AnyAsync(s => s.Underlying == symbol && s.TradeDate == tradeDate, ct);
            if (already) continue;

            try
            {
                // A wide strike window would be wasted: only the at-the-money strike is read.
                var chain = await SchwabOptionChain.FetchAsync(
                    _schwab, symbol, strikeCount: 6, httpFactory: _httpFactory, ct: ct);

                int written = 0;
                foreach (var expiry in chain.Expirations)
                {
                    var reading = AtTheMoney(chain, expiry);
                    if (reading is null) continue;

                    db.OptionChainSnapshots.Add(new OptionChainSnapshot
                    {
                        TradeDate        = tradeDate,
                        Underlying       = chain.Underlying,
                        UnderlyingPrice  = (double)chain.UnderlyingPrice,
                        Expiration       = DateOnly.FromDateTime(expiry),
                        DaysToExpiration = reading.Value.Dte,
                        AtmStrike        = (double)reading.Value.Strike,
                        AtmImpliedVol    = (double)reading.Value.Iv,
                        ExpectedMove     = chain.ExpectedMove(expiry) is { } em ? (double)em : null,
                    });
                    written++;
                }

                await db.SaveChangesAsync(ct);
                total += written;
                _log.LogInformation("Captured {Count} IV readings for {Symbol} on {Date}",
                    written, symbol, tradeDate);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Could not capture {Symbol}", symbol);
            }
        }
        return total;
    }

    /// <summary>
    /// At-the-money reading for one expiry. Averaging the call and put implied volatilities
    /// is steadier than either alone, which drifts with skew as spot moves off the strike.
    /// </summary>
    private static (decimal Strike, decimal Iv, int Dte)? AtTheMoney(OptionChain chain, DateTime expiry)
    {
        // Series-aware pairing lives on the chain, so index expirations that list both an
        // AM- and a PM-settled series at one strike do not collide.
        if (chain.AtTheMoneyPair(expiry) is not { } pair) return null;

        var (call, put) = pair;
        if (call.ImpliedVolatility <= 0m || put.ImpliedVolatility <= 0m) return null;

        return (call.Strike, (call.ImpliedVolatility + put.ImpliedVolatility) / 2m, call.DaysToExpiration);
    }
}
