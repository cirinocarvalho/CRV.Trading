namespace CRV.Core.Models;

/// <summary>
/// Lightweight write-once log of broker strategies placed.
/// Used to recover active trades on restart by re-discovering legs via REST.
/// Only stores the metadata needed to call DiscoverBracketLegsAsync — all
/// live order state comes from the broker REST API, not from the DB.
/// </summary>
public class StrategyLog
{
    public int     Id                 { get; set; }
    public string  BrokerStrategyId   { get; set; } = "";
    public string  SetupId            { get; set; } = "";
    public string  Ticker             { get; set; } = "";
    public string  DirectionStr       { get; set; } = "Long";
    public int     TotalContracts     { get; set; }
    public int     PartialContracts   { get; set; }
    public bool    UseBe              { get; set; }
    public decimal PointValue         { get; set; }
    public DateTime CreatedAt         { get; set; } = DateTime.UtcNow;
    public bool    IsCompleted        { get; set; }

    // Helpers
    public Direction Direction
    {
        get => Enum.TryParse<Direction>(DirectionStr, out var d) ? d : Direction.Long;
        set => DirectionStr = value.ToString();
    }
}
