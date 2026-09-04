namespace CRV.Core.Options;

/// <summary>
/// One structure the chain can build, priced and scored against a stated view.
/// </summary>
/// <param name="PnlAtTarget">
/// Dollars at expiration if the underlying finishes exactly at the target. This is the
/// ranking key, and on its own it is misleading: it always flatters structures that pay
/// only at a precise price. Read it next to <paramref name="Sensitivity"/>.
/// </param>
/// <param name="Sensitivity">
/// Payoff below, at, and above the target, so a candidate that wins narrowly is visibly
/// different from one that wins across a range.
/// </param>
/// <param name="WorstSpreadPct">
/// Widest bid/ask spread among the legs. A structure is only as executable as its worst leg.
/// </param>
public sealed record StructureCandidate(
    string  Name,
    IReadOnlyList<OptionLeg> Legs,
    decimal NetDebit,
    decimal? MaxLoss,
    decimal? MaxProfit,
    IReadOnlyList<decimal> Breakevens,
    decimal PnlAtTarget,
    IReadOnlyList<PayoffPoint> Sensitivity,
    decimal WorstSpreadPct)
{
    /// <summary>Profit at target as a percentage of what is at risk. Null when risk is unbounded.</summary>
    public decimal? ReturnOnRisk => MaxLoss is > 0m ? PnlAtTarget / MaxLoss.Value * 100m : null;
}

/// <summary>
/// Builds every structure the chain supports for a stated view and scores each one.
/// <para>It ranks; it does not recommend. The ordering is a function of the target price
/// you supply — change the target and the ordering changes — so the numbers are shown
/// side by side rather than reduced to a single suggestion.</para>
/// </summary>
public static class StructureFinder
{
    /// <param name="target">Where you expect the underlying to finish.</param>
    /// <param name="band">Fraction of the underlying used for the sensitivity points either side of the target.</param>
    public static IReadOnlyList<StructureCandidate> Find(
        OptionChain chain,
        DateTime    expiration,
        decimal     target,
        decimal     commissionPerContract = 0m,
        LiquidityGate? gate = null,
        decimal     band = 0.02m)
    {
        gate ??= new LiquidityGate();

        var calls = chain.For(expiration, OptionRight.Call).Where(gate.Admits).ToList();
        var puts  = chain.For(expiration, OptionRight.Put ).Where(gate.Admits).ToList();
        if (calls.Count == 0 && puts.Count == 0) return [];

        decimal spot   = chain.UnderlyingPrice;
        decimal offset = Math.Max(spot * band, 0.01m);
        var     found  = new List<StructureCandidate>();

        void Add(string name, params (OptionContract C, LegAction A, int Q)[] legs)
        {
            if (legs.Length == 0 || legs.Any(l => l.C is null)) return;
            // A structure that reuses a strike on the same side is degenerate, not a spread.
            if (legs.Select(l => (l.C.Symbol, l.A)).Distinct().Count() != legs.Length) return;

            var built = legs.Select(l => new OptionLeg(
                l.C.Right, l.A, l.C.Strike,
                // Buying lifts the ask, selling hits the bid.
                l.A == LegAction.Buy ? l.C.Ask : l.C.Bid,
                l.Q, l.C.Multiplier, l.C.Symbol)).ToList();

            var a = PayoffCalculator.Analyze(built, commissionPerContract);

            found.Add(new StructureCandidate(
                Name:        name,
                Legs:        built,
                NetDebit:    a.NetDebit,
                MaxLoss:     a.LossUnbounded   ? null : a.MaxLoss,
                MaxProfit:   a.ProfitUnbounded ? null : a.MaxProfit,
                Breakevens:  a.Breakevens,
                PnlAtTarget: PayoffCalculator.PayoffAt(built, target, commissionPerContract),
                Sensitivity:
                [
                    new PayoffPoint(target - offset, PayoffCalculator.PayoffAt(built, target - offset, commissionPerContract)),
                    new PayoffPoint(target,          PayoffCalculator.PayoffAt(built, target,          commissionPerContract)),
                    new PayoffPoint(target + offset, PayoffCalculator.PayoffAt(built, target + offset, commissionPerContract)),
                ],
                WorstSpreadPct: legs.Max(l => l.C.SpreadPct)));
        }

        bool bullish = target > spot;
        bool bearish = target < spot;

        // Strikes are chosen by proximity so the set adapts to whatever the chain lists.
        OptionContract? Near(List<OptionContract> xs, decimal price)
            => xs.Count == 0 ? null : xs.MinBy(c => Math.Abs(c.Strike - price));

        var atmCall    = Near(calls, spot);
        var atmPut     = Near(puts,  spot);
        var targetCall = Near(calls, target);
        var targetPut  = Near(puts,  target);

        if (bullish)
        {
            if (atmCall is not null)
                Add("Long call", (atmCall, LegAction.Buy, 1));

            if (atmCall is not null && targetCall is not null && targetCall.Strike > atmCall.Strike)
                Add("Bull call spread", (atmCall, LegAction.Buy, 1), (targetCall, LegAction.Sell, 1));

            // Credit put spread below spot: pays if the move simply does not go against you.
            var shortPut = Near(puts, spot - offset);
            var longPut  = Near(puts, spot - offset * 2m);
            if (shortPut is not null && longPut is not null && longPut.Strike < shortPut.Strike)
                Add("Bull put spread (credit)", (shortPut, LegAction.Sell, 1), (longPut, LegAction.Buy, 1));
        }

        if (bearish)
        {
            if (atmPut is not null)
                Add("Long put", (atmPut, LegAction.Buy, 1));

            if (atmPut is not null && targetPut is not null && targetPut.Strike < atmPut.Strike)
                Add("Bear put spread", (atmPut, LegAction.Buy, 1), (targetPut, LegAction.Sell, 1));

            var shortCall = Near(calls, spot + offset);
            var longCall  = Near(calls, spot + offset * 2m);
            if (shortCall is not null && longCall is not null && longCall.Strike > shortCall.Strike)
                Add("Bear call spread (credit)", (shortCall, LegAction.Sell, 1), (longCall, LegAction.Buy, 1));
        }

        // Centred on the target: the structure that pays most if the price lands exactly
        // there, and least if it does not. Offered for any view, including a flat one.
        var body = Near(calls, target);
        if (body is not null)
        {
            var lower = Near(calls, body.Strike - offset);
            var upper = Near(calls, body.Strike + offset);
            if (lower is not null && upper is not null &&
                lower.Strike < body.Strike && upper.Strike > body.Strike)
                Add("Butterfly at target",
                    (lower, LegAction.Buy, 1), (body, LegAction.Sell, 2), (upper, LegAction.Buy, 1));
        }

        if (!bullish && !bearish)
        {
            // Target sits on spot: the view is that nothing much happens.
            var shortPut  = Near(puts,  spot - offset);
            var longPut   = Near(puts,  spot - offset * 2m);
            var shortCall = Near(calls, spot + offset);
            var longCall  = Near(calls, spot + offset * 2m);
            if (shortPut is not null && longPut is not null && shortCall is not null && longCall is not null)
                Add("Iron condor",
                    (shortPut, LegAction.Sell, 1), (longPut, LegAction.Buy, 1),
                    (shortCall, LegAction.Sell, 1), (longCall, LegAction.Buy, 1));
        }
        else if (atmCall is not null && atmPut is not null)
        {
            // A large move in either direction also pays here, so it belongs on the list.
            Add("Straddle", (atmCall, LegAction.Buy, 1), (atmPut, LegAction.Buy, 1));
        }

        return found.OrderByDescending(c => c.PnlAtTarget).ToList();
    }
}
