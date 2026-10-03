using CRV.Core.Models;
using CRV.Core.Strategy;
using CRV.Live;

namespace CRV.Web.Services;

public sealed record BasketItem(BasketEntry Entry, bool IsEmaBasket);
public sealed record BasketChange(bool Ok, string? Error, IReadOnlyList<string> Warnings);

/// <summary>
/// Edits the setup basket one entry at a time. Each change reads the saved baskets, changes one
/// entry, and writes both back through <see cref="BasketCodec"/>, so editing one setup can't
/// disturb another and a basket is never replaced by a stale copy from an open page.
/// </summary>
public sealed class StrategyBasketService
{
    private readonly StrategyConfigService           _cfgSvc;
    private readonly LiveEngineOrchestrator          _engine;
    private readonly ILogger<StrategyBasketService>  _log;
    private readonly object                          _lock = new();

    public StrategyBasketService(StrategyConfigService cfgSvc, LiveEngineOrchestrator engine, ILogger<StrategyBasketService> log)
    {
        _cfgSvc = cfgSvc; _engine = engine; _log = log;
    }

    public List<BasketItem> All()
    {
        var c = _cfgSvc.Current;
        return BasketCodec.Parse(c.BasketJson).Select(e => new BasketItem(e, false))
            .Concat(BasketCodec.Parse(c.EmaBasketJson).Select(e => new BasketItem(e, true)))
            .ToList();
    }

    public BasketItem? Find(string id) => All().FirstOrDefault(i => i.Entry.Id == id);

    public BasketChange Update(string id, Action<BasketEntry> apply, string who) =>
        Change(who, $"edit {id}", (orb, ema, _) =>
        {
            var entry = orb.FirstOrDefault(e => e.Id == id) ?? ema.FirstOrDefault(e => e.Id == id);
            if (entry == null) return "That strategy no longer exists. It may have been removed on another page.";
            apply(entry);
            return null;
        });

    /// <summary>Replace one entry with an edited copy (same Id), leaving every other entry untouched.
    /// Refused when the edited entry can't trade (<see cref="SetupValidation.SaveErrors"/>).</summary>
    public BasketChange Replace(string id, BasketEntry edited, string who) =>
        Change(who, $"edit {id}", (orb, ema, cfg) =>
        {
            edited.Id = id;
            foreach (var list in new[] { orb, ema })
            {
                var i = list.FindIndex(e => e.Id == id);
                if (i < 0) continue;
                var problems = SetupValidation.SaveErrors(edited, cfg);
                if (problems.Count > 0) return SetupValidation.Sentences(problems);
                list[i] = edited;
                return null;
            }
            return "That strategy no longer exists. It may have been removed on another page.";
        });

    /// <summary>Switching on is refused when the entry can't trade. Switching off never is.</summary>
    public BasketChange SetEnabled(string id, bool enabled, string who) =>
        Change(who, $"edit {id}", (orb, ema, cfg) =>
        {
            var entry = orb.FirstOrDefault(e => e.Id == id) ?? ema.FirstOrDefault(e => e.Id == id);
            if (entry == null) return "That strategy no longer exists. It may have been removed on another page.";
            entry.Enabled = enabled;
            if (!enabled) return null;
            var problems = SetupValidation.SaveErrors(entry, cfg);
            return problems.Count > 0 ? SetupValidation.Sentences(problems) : null;
        });

    public (BasketChange Change, string? Id) Add(StrategyType type, string ticker, decimal pointValue, decimal tickSize, string who)
    {
        string? newId = null;
        var change = Change(who, $"add {type} {ticker}", (orb, ema, _) =>
        {
            var root = FuturesSymbol.RootSymbol(ticker).ToLowerInvariant();
            var stem = $"{type.ToString().ToLowerInvariant()}-{root}";
            var taken = orb.Concat(ema).Select(e => e.Id).ToHashSet();
            newId = stem;
            for (var n = 2; taken.Contains(newId); n++) newId = $"{stem}-{n}";

            var entry = new BasketEntry
            {
                Id = newId, Enabled = false, Label = newId, StrategyType = type,
                Ticker = ticker, PointValue = pointValue, TickSize = tickSize,
                // Same starting values the old basket editor used for a new entry.
                Config = new StrategySetupConfig
                {
                    MaxTrades = 5, CutoffHour = 14, CutoffMinute = 30, Contracts = 2, MaxContracts = 2, AutoSizeByRisk = false,
                    HiVolMult = 1, StopPct = 0.35m, TargetPct = 65, PartialPct = 50, MinRr = 1.5m, Mode = "Aggressive",
                    OrderType = "Market", UsePartial = true, UseBe = true, PartialCts = 1, MaxEntrySlippage = 0,
                    MaxTradeRisk = 0, StopMode = "OrbPct", StopVwapTicks = 4,
                },
            };
            (type == StrategyType.Ema ? ema : orb).Add(entry);
            return null;
        });
        return (change, change.Ok ? newId : null);
    }

    public BasketChange Remove(string id, string who) =>
        Change(who, $"remove {id}", (orb, ema, _) =>
        {
            if (orb.RemoveAll(e => e.Id == id) > 0)
            {
                // An empty ORB basket makes the engine fall back to the legacy A–D setups.
                if (orb.Count == 0)
                    return "This is your last opening-range strategy. Turn it off instead: with none left, the engine falls back to the old A–D setups.";
                return null;
            }
            return ema.RemoveAll(e => e.Id == id) > 0 ? null : "That strategy no longer exists.";
        });

    private BasketChange Change(string who, string what, Func<List<BasketEntry>, List<BasketEntry>, StrategyConfig, string?> edit)
    {
        lock (_lock)
        {
            var cfg = _cfgSvc.Current.Clone();
            List<BasketEntry> orb, ema;
            try
            {
                orb = BasketCodec.Parse(cfg.BasketJson);
                ema = BasketCodec.Parse(cfg.EmaBasketJson);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Strategy basket could not be read; nothing changed");
                return new BasketChange(false, "The saved strategy list couldn't be read, so nothing was changed. Check the app log.", Array.Empty<string>());
            }

            var error = edit(orb, ema, cfg);
            if (error != null) return new BasketChange(false, error, Array.Empty<string>());

            cfg.BasketJson      = BasketCodec.Serialize(orb);
            cfg.EmaBasketJson = BasketCodec.Serialize(ema);
            _cfgSvc.Update(cfg);
            _engine.ApplyRuntimeSettings(cfg);
            _log.LogWarning("Strategies: {Who} {What}", who, what);
            return new BasketChange(true, null, cfg.Validate());
        }
    }
}
