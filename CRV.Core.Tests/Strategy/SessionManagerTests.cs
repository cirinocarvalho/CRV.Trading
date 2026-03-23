namespace CRV.Core.Tests.Strategy;

using CRV.Core.Models;
using CRV.Core.Strategy;
using Xunit;

public class SessionManagerTests
{
    private static List<SessionConfig> ThreeSessions() => new()
    {
        new() { SessionId = SessionId.Asia,   Enabled = true, RthStart = new(19, 0), RthEnd = new(23, 59), OrbStart = new(19, 0), OrbEnd = new(19, 30) },
        new() { SessionId = SessionId.London, Enabled = true, RthStart = new(3, 0),  RthEnd = new(8, 0),   OrbStart = new(3, 0),  OrbEnd = new(3, 30)  },
        new() { SessionId = SessionId.NY,     Enabled = true, RthStart = new(9, 30), RthEnd = new(16, 0),  OrbStart = new(9, 30), OrbEnd = new(10, 0)  },
    };

    [Theory]
    [InlineData(19, 15, SessionId.Asia)]
    [InlineData(23, 58, SessionId.Asia)]
    [InlineData(3, 0,  SessionId.London)]
    [InlineData(7, 59, SessionId.London)]
    [InlineData(9, 30, SessionId.NY)]
    [InlineData(15, 59, SessionId.NY)]
    public void GetActiveSession_ReturnsCorrectSession(int hour, int min, SessionId expected)
    {
        var mgr = new SessionManager(ThreeSessions());
        var result = mgr.GetActiveSession(new TimeOnly(hour, min));
        Assert.NotNull(result);
        Assert.Equal(expected, result!.SessionId);
    }

    [Theory]
    [InlineData(0, 30)]   // gap: Asia->London
    [InlineData(2, 59)]   // just before London
    [InlineData(8, 30)]   // gap: London->NY
    [InlineData(16, 30)]  // gap: NY->Asia
    [InlineData(18, 30)]  // gap: NY->Asia
    public void GetActiveSession_ReturnsNull_InGaps(int hour, int min)
    {
        var mgr = new SessionManager(ThreeSessions());
        var result = mgr.GetActiveSession(new TimeOnly(hour, min));
        Assert.Null(result);
    }

    [Fact]
    public void GetActiveSession_SkipsDisabledSessions()
    {
        var sessions = ThreeSessions();
        sessions[0].Enabled = false; // disable Asia
        var mgr = new SessionManager(sessions);
        var result = mgr.GetActiveSession(new TimeOnly(19, 30));
        Assert.Null(result); // Asia disabled, nothing active at 19:30
    }

    [Fact]
    public void Validate_RejectsOverlappingSessions()
    {
        var sessions = new List<SessionConfig>
        {
            new() { SessionId = SessionId.Asia,   Enabled = true, RthStart = new(19, 0), RthEnd = new(23, 59) },
            new() { SessionId = SessionId.London, Enabled = true, RthStart = new(23, 0), RthEnd = new(8, 0) }, // overlaps Asia AND spans midnight
        };
        var errors = SessionManager.Validate(sessions);
        Assert.NotEmpty(errors);
    }

    [Fact]
    public void Validate_AcceptsMidnightSpanning()
    {
        var sessions = new List<SessionConfig>
        {
            new() { SessionId = SessionId.Asia, Enabled = true, RthStart = new(22, 0), RthEnd = new(2, 0),
                    OrbStart = new(22, 0), OrbEnd = new(23, 0) },
        };
        var errors = SessionManager.Validate(sessions);
        Assert.Empty(errors);
    }

    [Fact]
    public void GetActiveSession_MidnightSpanning_BeforeMidnight()
    {
        var sessions = new List<SessionConfig>
        {
            new() { SessionId = SessionId.Asia, Enabled = true, RthStart = new(19, 0), RthEnd = new(2, 0) },
        };
        var mgr = new SessionManager(sessions);
        Assert.Equal(SessionId.Asia, mgr.GetActiveSession(new(23, 30))?.SessionId);
    }

    [Fact]
    public void GetActiveSession_MidnightSpanning_AfterMidnight()
    {
        var sessions = new List<SessionConfig>
        {
            new() { SessionId = SessionId.Asia, Enabled = true, RthStart = new(19, 0), RthEnd = new(2, 0) },
        };
        var mgr = new SessionManager(sessions);
        Assert.Equal(SessionId.Asia, mgr.GetActiveSession(new(0, 20))?.SessionId);
    }

    [Fact]
    public void GetActiveSession_MidnightSpanning_OutsideWindow()
    {
        var sessions = new List<SessionConfig>
        {
            new() { SessionId = SessionId.Asia, Enabled = true, RthStart = new(19, 0), RthEnd = new(2, 0) },
        };
        var mgr = new SessionManager(sessions);
        Assert.Null(mgr.GetActiveSession(new(15, 0)));
    }

    [Fact]
    public void Validate_AcceptsValidSessions()
    {
        var errors = SessionManager.Validate(ThreeSessions());
        Assert.Empty(errors);
    }
}
