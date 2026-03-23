namespace CRV.Core.Models;

/// <summary>
/// Persisted order record — stores both mock and (future) real-broker orders
/// so the Orders page can query history across brokers.
/// </summary>
public class OrderRecord
{
    public int       Id          { get; set; }
    public string    OrderId     { get; set; } = "";
    public string    Broker      { get; set; } = "Mock";  // Mock | Schwab | TradeStation | Tradovate
    public string    Symbol      { get; set; } = "";
    public string    Action      { get; set; } = "";      // BUY | SELL
    public int       Quantity    { get; set; }
    public decimal?  LimitPrice  { get; set; }
    public decimal?  StopPrice   { get; set; }
    public string    Status      { get; set; } = "WORKING"; // WORKING | FILLED | CANCELED
    public decimal?  FillPrice   { get; set; }
    public DateTime  PlacedAt    { get; set; } = DateTime.UtcNow;
    public DateTime? FilledAt    { get; set; }
    public string?   OcoGroupId  { get; set; }
    public string?   SessionId   { get; set; }
    public string?   Direction   { get; set; }  // "Long" | "Short" — for restart recovery
    public string?   SetupId     { get; set; }  // "A" | "B" | etc. — for Orders page display

    public string OrderType => (LimitPrice.HasValue, StopPrice.HasValue) switch
    {
        (true,  false) => "LIMIT",
        (false, true)  => "STOP",
        (true,  true)  => "STOP_LIMIT",
        _              => "MARKET"
    };
}
