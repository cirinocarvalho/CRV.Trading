using CRV.Core.Models;

namespace CRV.Backtest.Engine;

/// <summary>
/// Prices simulated fills.
/// <para>
/// The engine used to book every exit at its exact order price, so a stop at 18000
/// always filled at 18000 and every stop-out cost precisely 1R. Live, 20 of 123
/// stop-outs exceeded 1R and the worst reached -4.32R: a stop is a market order once
/// touched, and it is touched in a market that is already moving away. A backtest
/// that charges nothing for that is not conservative, it is wrong, and it flatters
/// exactly the strategies that stop out most.
/// </para>
/// <para>
/// Limit orders are treated as never filling worse than their price, which is what a
/// limit order guarantees. The optimism that remains is the reverse case — a resting
/// limit that live would have missed entirely still fills here.
/// </para>
/// </summary>
public sealed class ExecutionModel
{
    private readonly BacktestConfig _cfg;
    private readonly Func<string, decimal> _tickSizeFor;

    public ExecutionModel(BacktestConfig cfg, Func<string, decimal> tickSizeFor)
    {
        _cfg         = cfg;
        _tickSizeFor = tickSizeFor;
    }

    /// <summary>Single-instrument convenience for tests and single-ticker runs.</summary>
    public ExecutionModel(BacktestConfig cfg, decimal tickSize)
        : this(cfg, _ => tickSize) { }

    private bool SlippageOn => _cfg.FillMode == FillMode.WithSlippage;

    /// <summary>Price paid on an entry. Limit entries fill at their limit; market entries pay up.</summary>
    public decimal EntryFill(decimal orderPrice, bool isBuy, bool isLimit, string ticker = "")
    {
        if (isLimit || !SlippageOn) return orderPrice;
        return Adverse(orderPrice, isBuy, _cfg.SlippageTicks, ticker);
    }

    /// <summary>
    /// Price received on an exit. Stops slip by <see cref="BacktestConfig.StopSlippageTicks"/>;
    /// targets are limit orders and fill at their level.
    /// </summary>
    public decimal ExitFill(LegType leg, bool isBuy, decimal orderPrice, string ticker = "")
    {
        if (!SlippageOn || leg != LegType.Stop) return orderPrice;
        return Adverse(orderPrice, isBuy, _cfg.StopSlippageTicks, ticker);
    }

    /// <summary>An exit taken at market — a session-end flatten, say — pays the entry slippage.</summary>
    public decimal MarketExitFill(decimal price, bool isBuy, string ticker = "")
        => SlippageOn ? Adverse(price, isBuy, _cfg.SlippageTicks, ticker) : price;

    // Buying costs more, selling receives less. Always against the position.
    private decimal Adverse(decimal price, bool isBuy, int ticks, string ticker)
    {
        decimal slip = ticks * _tickSizeFor(ticker);
        return isBuy ? price + slip : price - slip;
    }
}
