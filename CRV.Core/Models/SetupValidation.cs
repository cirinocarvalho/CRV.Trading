using CRV.Core.Strategy;

namespace CRV.Core.Models;

/// <summary>Checks basket entries before they're saved or started.</summary>
public static class SetupValidation
{
    /// <summary>The number the EMA21 strategy had. Reserved so stored baskets and saved backtest runs never reuse it.</summary>
    public const StrategyType RetiredEma21 = (StrategyType)4;

    /// <summary>Why an entry of the retired type doesn't trade.</summary>
    public const string RetiredEma21Reason = "retired EMA21 strategy";
}
