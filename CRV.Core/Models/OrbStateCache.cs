namespace CRV.Core.Models;

public class OrbStateCache
{
    public DateTime TradingDate { get; set; }
    public string Symbol { get; set; } = "";
    public string SessionId { get; set; } = "";  // empty = legacy single-session
    public decimal OrbHigh { get; set; }
    public decimal OrbLow { get; set; }
    public decimal CloseRelPct { get; set; }
    public decimal OrbAtrRatio { get; set; }
    // Opening Drive state (cached so it survives engine restart)
    public bool OpeningDriveBull { get; set; }
    public bool OpeningDriveBear { get; set; }
    public decimal DriveRangePctATR { get; set; }

    // Prior-session reference ranges captured at ORB-save time, so SessionFakeout
    // setups have a stable historical record of the range they sized against.
    // Populated from SessionEngine — at the moment a session's ORB forms, the
    // PRIOR session has already closed, so its full range is known. Earlier
    // sessions of the same trading day will have the unknown values at 0.
    public decimal PrevDayHigh { get; set; }   // PDH (prior NY day)
    public decimal PrevDayLow  { get; set; }   // PDL
    public decimal AsiaHigh    { get; set; }   // 0 until end of Asia
    public decimal AsiaLow     { get; set; }   // 0 until end of Asia
    public decimal LondonHigh  { get; set; }   // 0 until end of London
    public decimal LondonLow   { get; set; }   // 0 until end of London

    public DateTime SavedAtUtc { get; set; }
}
