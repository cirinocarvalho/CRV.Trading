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
    public DateTime SavedAtUtc { get; set; }
}
