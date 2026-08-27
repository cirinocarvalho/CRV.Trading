namespace CRV.Core.Models;

/// <summary>
/// A locally persisted record of one option structure submitted to a broker.
/// <para>The broker is authoritative for order state — this exists to keep what the
/// broker never stores: that these legs were ONE structure, what it was expected to
/// cost, and what its worst case was at the moment it was confirmed. Schwab's order
/// history returns legs; it does not return the fact that they formed a butterfly with
/// a $54.60 max loss.</para>
/// <para>Rows are immutable once written. Order status is always read live from the
/// broker, never mirrored here, so the two cannot drift.</para>
/// </summary>
public class OptionOrderRecord
{
    public int      Id         { get; set; }
    public string   Broker     { get; set; } = "Schwab";

    /// <summary>Broker order id, when the broker returned one.</summary>
    public string?  OrderId    { get; set; }

    public string   Underlying { get; set; } = "";

    /// <summary>Detected shape at submission time — Butterfly, Vertical, Iron condor…</summary>
    public string   Structure  { get; set; } = "";

    /// <summary>"Open" or "Close".</summary>
    public string   Intent     { get; set; } = "Open";

    /// <summary>Number of spreads submitted; leg quantities are ratios times this.</summary>
    public int      Spreads    { get; set; } = 1;

    public string   OrderType  { get; set; } = "";

    /// <summary>Net premium per spread as sent to the broker (always positive; sign is in OrderType).</summary>
    public decimal  NetPrice   { get; set; }

    /// <summary>Total debit (positive) or credit (negative) in dollars, commission included.</summary>
    public decimal  TotalNet   { get; set; }

    /// <summary>Worst case in dollars at submission. Null when the structure had unlimited downside.</summary>
    public decimal? MaxLoss    { get; set; }

    /// <summary>Best case in dollars at submission. Null when unbounded.</summary>
    public decimal? MaxProfit  { get; set; }

    /// <summary>Breakeven prices, comma separated, as computed when the order was confirmed.</summary>
    public string?  Breakevens { get; set; }

    /// <summary>The submitted legs as JSON — instruction, symbol, quantity, premium.</summary>
    public string   LegsJson   { get; set; } = "[]";

    public bool     Accepted   { get; set; }

    /// <summary>Broker response when the order was refused.</summary>
    public string?  Error      { get; set; }

    public DateTime PlacedAt   { get; set; } = DateTime.UtcNow;
}
