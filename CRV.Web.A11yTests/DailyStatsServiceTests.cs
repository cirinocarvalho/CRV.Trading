using CRV.Core.Models;
using CRV.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CRV.Web.A11yTests;

/// <summary>Today's stats follow the engine's trading day (18:00 ET roll), not the UTC date.</summary>
public class DailyStatsServiceTests
{
    private static readonly StrategyConfig Cfg = new() { Timezone = "America/New_York", SessionStartHour = 18, MaxDailyLoss = 500m };

    private static TradeRecord Closed(DateTime exitUtc, decimal net) => new()
    {
        SetupLabel = "retest-mnq", GrossPnl = net, NetPnl = net, EnteredAt = exitUtc.AddMinutes(-30), ExitedAt = exitUtc,
    };

    [Fact]
    public void TradesEitherSideOfUtcMidnight_InOneTradingDay_CountTogether()
    {
        var svc = new DailyStatsService();
        svc.OnTradeClosed(Closed(new DateTime(2026, 4, 15, 23, 0, 0, DateTimeKind.Utc), -300m), Cfg);   // 19:00 ET → the 16th
        svc.OnTradeClosed(Closed(new DateTime(2026, 4, 16, 1, 0, 0, DateTimeKind.Utc), -250m), Cfg);    // 21:00 ET → the 16th

        var s = svc.Get();
        Assert.Equal(new DateTime(2026, 4, 16), s.Date);
        Assert.Equal(2, s.TodayTrades);
        Assert.Equal(-550m, s.TodayNetPnL);
        Assert.True(s.DDBreached);
    }

    [Fact]
    public void ATradeAfterTheSessionRoll_StartsANewDay()
    {
        var svc = new DailyStatsService();
        svc.OnTradeClosed(Closed(new DateTime(2026, 4, 15, 21, 30, 0, DateTimeKind.Utc), -300m), Cfg);  // 17:30 ET → the 15th
        svc.OnTradeClosed(Closed(new DateTime(2026, 4, 15, 22, 30, 0, DateTimeKind.Utc), -100m), Cfg);  // 18:30 ET → the 16th

        var s = svc.Get();
        Assert.Equal(new DateTime(2026, 4, 16), s.Date);
        Assert.Equal(1, s.TodayTrades);
        Assert.Equal(-100m, s.TodayNetPnL);
    }

    [Fact]
    public void ATradeOpenedBeforeTheRollAndClosedAfter_CountsOnTheDayItClosed()
    {
        var svc = new DailyStatsService();
        var trade = new TradeRecord
        {
            SetupLabel = "retest-mnq", GrossPnl = -200m, NetPnl = -200m,
            EnteredAt = new DateTime(2026, 4, 15, 21, 0, 0, DateTimeKind.Utc),   // 17:00 ET → the 15th
            ExitedAt  = new DateTime(2026, 4, 15, 23, 0, 0, DateTimeKind.Utc),   // 19:00 ET → the 16th
        };

        svc.OnTradeClosed(trade, Cfg);

        var s = svc.Get();
        Assert.Equal(new DateTime(2026, 4, 16), s.Date);
        Assert.Equal(-200m, s.TodayNetPnL);
    }

    [Fact]
    public async Task AClosedTradeReachingTheEmailSink_IsCountedOnce()
    {
        var stats = new DailyStatsService();
        var cfgSvc = new StrategyConfigService(new ServiceCollection().BuildServiceProvider(), NullLogger<StrategyConfigService>.Instance);
        var email = new EmailNotificationService(
            new StaticOptionsMonitor(new SmtpSettings()), cfgSvc, stats,
            new ServiceCollection().BuildServiceProvider(), NullLogger<EmailNotificationService>.Instance);
        var exitUtc = new DateTime(2026, 4, 15, 23, 0, 0, DateTimeKind.Utc);

        await email.OnExitAsync(Closed(exitUtc, -120m));

        Assert.Equal(1, stats.Get().TodayTrades);
        Assert.Equal(-120m, stats.Get().TodayNetPnL);
    }

    private sealed class StaticOptionsMonitor(SmtpSettings value) : IOptionsMonitor<SmtpSettings>
    {
        public SmtpSettings CurrentValue => value;
        public SmtpSettings Get(string? name) => value;
        public IDisposable? OnChange(Action<SmtpSettings, string?> listener) => null;
    }
}
