namespace CRV.Core.Models;

/// <summary>OHLCV bar — immutable. IsConfirmed = true means the bar has closed.</summary>
public record Bar(
    DateTime Time,
    decimal  Open,
    decimal  High,
    decimal  Low,
    decimal  Close,
    long     Volume,
    bool     IsConfirmed = true,
    string   Ticker = "")
{
    public decimal HLC3 => (High + Low + Close) / 3m;

    public decimal TrueRange(Bar? prev)
    {
        if (prev is null) return High - Low;
        return Math.Max(High - Low,
               Math.Max(Math.Abs(High - prev.Close),
                        Math.Abs(Low  - prev.Close)));
    }
}
