namespace CRV.Core.Models;

public class AutoTrailConfig
{
    public bool Enabled { get; set; }
    public decimal StopLoss { get; set; }
    public decimal Freq { get; set; }
    public decimal? Trigger { get; set; }
}
