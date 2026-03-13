namespace CRV.Core.Modules;

public class ModuleConfig
{
    // Session times (Eastern Time hours/minutes)
    public TimeOnly AsiaStart    { get; set; } = new(18, 0);
    public TimeOnly AsiaEnd      { get; set; } = new(0, 0);
    public TimeOnly LondonStart  { get; set; } = new(3, 0);
    public TimeOnly LondonEnd    { get; set; } = new(8, 30);
    public TimeOnly NYOpenStart  { get; set; } = new(9, 30);
    public TimeOnly NYOpenEnd    { get; set; } = new(11, 30);
    public TimeOnly MiddayStart  { get; set; } = new(11, 30);
    public TimeOnly MiddayEnd    { get; set; } = new(13, 30);
    public TimeOnly PowerStart   { get; set; } = new(15, 0);
    public TimeOnly PowerEnd     { get; set; } = new(16, 0);

    // Sweep detector
    public decimal MinTickPenetration  { get; set; } = 0.50m;
    public decimal MinBodyReject       { get; set; } = 1.00m;
    public decimal EqualLevelTolerance { get; set; } = 2.00m;
    public int     ConfirmationBars    { get; set; } = 1;

    // Opening drive
    public decimal DriveRangeAtrMult   { get; set; } = 0.80m;
    public decimal MaxDrivePullback    { get; set; } = 0.35m;
    public int     DriveBullBearRatio  { get; set; } = 2;

    // Trend day
    public int     TrendDayThreshold   { get; set; } = 4;
    public decimal ShallowPullbackMax  { get; set; } = 0.35m;

    // VWAP
    public int     VwapDevPeriod       { get; set; } = 20;

    // Instrument (set from StrategyConfig)
    public decimal TickSize   { get; set; } = 0.25m;
    public decimal PointValue { get; set; } = 20m;
    public string  Timezone   { get; set; } = "America/New_York";
}
