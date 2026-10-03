using System.Globalization;
using CRV.Core.Data;
using CRV.Core.Models;
using CRV.Core.Strategy;
using CRV.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CRV.Web.Pages.Setup;

/// <summary>Edit one basket strategy. Saving replaces only this entry.</summary>
public class StrategyModel : PageModel
{
    /// <summary>Settings stored as fractions of the range but shown and entered as percentages.</summary>
    public static readonly string[] FractionFields = { "StopPct", "NearPct", "PullbackPct", "RetestPct", "MaxEntrySlippage" };
    public static readonly string[] SessionNames = { "Asia", "London", "NY" };

    /// <summary>Types this page can edit. Any other (the retired EMA21, or one this version can't run) is shown read-only.</summary>
    public static readonly StrategyType[] EditableTypes =
        { StrategyType.Pullback, StrategyType.Retest, StrategyType.OrbFakeout, StrategyType.SessionFakeout };

    /// <summary>How many recent backtest runs the typical stop is read from.</summary>
    private const int RunsScanned = 20;

    public const string GuardOnHelp     = "Checks the target when you save, and every trade before it's sent.";
    public const string GuardOffHelp    = "Off: no check on save, and trades are taken whatever their reward / risk. Your minimum R and what to do below it are kept for when you turn it back on.";
    public const string ActionSkipHelp  = "Each trade is still checked: if its stop makes the target less than {r}R, the trade is skipped.";
    public const string ActionRaiseHelp = "Each trade is still checked: if its stop makes the target less than {r}R, the target moves out to {r}R for that trade.";
    public const string ActionOffHelp   = "Not enforced: turn on “Enforce minimum reward / risk” under Size and limits.";

    public static string ActionHelp(StrategySetupConfig c) =>
        (!c.EnforceMinRr ? ActionOffHelp : c.MinRrAction == MinRrAction.RaiseTarget ? ActionRaiseHelp : ActionSkipHelp)
        .Replace("{r}", c.MinRr.ToString("0.##", CultureInfo.InvariantCulture));

    private readonly StrategyBasketService _basket;
    private readonly StrategyConfigService _cfgSvc;
    private readonly LiveEngineOrchestrator _engine;
    private readonly TradingDbContext _db;

    public StrategyModel(StrategyBasketService basket, StrategyConfigService cfgSvc, LiveEngineOrchestrator engine, TradingDbContext db)
    {
        _basket = basket; _cfgSvc = cfgSvc; _engine = engine; _db = db;
    }

    [BindProperty(SupportsGet = true)] public string Id { get; set; } = "";

    public BasketEntry Entry { get; private set; } = new();
    public bool ReadOnly => !EditableTypes.Contains(Entry.StrategyType);
    /// <summary>Why the saved entry doesn't trade ("retired EMA21 strategy"); null when it trades or is simply off.</summary>
    public string? DisabledReason { get; private set; }
    /// <summary>"NQ / MNQ": the strategies that share this one's bar size.</summary>
    public string RootLabel => TickerGroup.GroupLabel(TickerGroup.GetGroupKey(Entry.Ticker));
    public bool EngineRunning => _engine.IsRunning;
    public List<string> Errors { get; } = new();
    public List<string> Warnings { get; } = new();
    public string? Saved { get; private set; }
    public string? RemoveError { get; private set; }
    /// <summary>Median stop of the last 30 backtest trades, in points; null without a backtest.</summary>
    public decimal? TypicalStopPoints { get; private set; }
    /// <summary>Median of contracts × stop over the same trades, in points.</summary>
    public decimal? TypicalPositionPoints { get; private set; }
    public int TypicalStopTrades { get; private set; }

    public IActionResult OnGet()
    {
        if (!Load()) return NotFound();
        LoadTypicalStop();
        Saved = TempData["strategy_saved"] as string;
        RemoveError = TempData["strategy_error"] as string;
        if (TempData["strategy_warnings"] is string w) Warnings.AddRange(w.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync()
    {
        if (!Load()) return NotFound();
        if (ReadOnly) return RedirectToPage(new { id = Id });
        var before = Entry;

        // Bind onto a copy of the saved entry: anything the form doesn't post keeps its value.
        var edited = BasketCodec.Parse(BasketCodec.Serialize(new[] { before }))[0];
        var hadTrail = before.AutoTrail != null;
        var hadSessions = before.Sessions is { Count: > 0 };
        await TryUpdateModelAsync(edited, "Entry");
        // The label is optional; a blank box binds as null and trips the implicit "required" check.
        ModelState.Remove("Entry.Label");
        edited.Label ??= "";
        if (!ModelState.IsValid)
            Errors.AddRange(ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage).Where(m => m != ""));

        foreach (var f in FractionFields)
        {
            var raw = Request.Form["pct." + f].ToString();
            if (raw == "") continue;
            if (!decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var pct) || pct < 0)
            {
                Errors.Add($"{f}: enter a number, 0 or more.");
                continue;
            }
            typeof(StrategySetupConfig).GetProperty(f)!.SetValue(edited.Config, pct / 100m);
        }

        if (hadSessions)
        {
            for (var i = 0; i < SessionNames.Length; i++)
            {
                var slot = edited.Sessions.FirstOrDefault(s => s.SessionId == SessionNames[i]);
                var on = Request.Form[$"session.{i}.on"].Contains("true");
                if (slot == null && !on) continue;   // an absent session already means "not traded"
                if (slot == null) { slot = new SessionSlot { SessionId = SessionNames[i] }; edited.Sessions.Add(slot); }
                slot.Enabled = on;
                if (TimeOnly.TryParse(Request.Form[$"session.{i}.cutoff"], CultureInfo.InvariantCulture, out var t))
                {
                    slot.CutoffHour = t.Hour;
                    slot.CutoffMinute = t.Minute;
                }
            }
        }

        // A trail that was never set up and is still off stays absent rather than becoming an empty object.
        if (!hadTrail && edited.AutoTrail is { Enabled: false }) edited.AutoTrail = null;

        Validate(edited);
        LoadTypicalStop();
        var rr = MinRrSaveCheck.Check(edited.Config, edited.PointValue, TypicalStopPoints, TypicalPositionPoints);
        if (rr.Error != null) Errors.Add(rr.Error);
        if (Errors.Count > 0)
        {
            Entry = edited;
            return Page();
        }

        var change = _basket.Replace(Id, edited, Who);
        if (!change.Ok) { Errors.Add(change.Error!); Entry = edited; return Page(); }

        TempData["strategy_saved"] = RestartNote(before, edited);
        var warnings = rr.Warning is { } guardWarning ? change.Warnings.Append(guardWarning).ToList() : change.Warnings.ToList();
        if (warnings.Count > 0) TempData["strategy_warnings"] = string.Join("\n", warnings);
        return RedirectToPage(new { id = Id });
    }

    public IActionResult OnPostRemove()
    {
        var change = _basket.Remove(Id, Who);
        if (!change.Ok)
        {
            TempData["strategy_error"] = change.Error;
            return RedirectToPage(new { id = Id });
        }
        TempData["strategies_msg"] = $"Removed {Id}.";
        return RedirectToPage("/Setup/Strategies");
    }

    private string Who => User?.Identity?.Name ?? Request.Headers["X-MS-CLIENT-PRINCIPAL-NAME"].FirstOrDefault() ?? "unknown";

    private bool Load()
    {
        var item = _basket.Find(Id);
        if (item == null) return false;
        Entry = item.Entry;
        DisabledReason = SetupValidation.DisabledReason(Entry, _cfgSvc.Current);
        return true;
    }

    private void LoadTypicalStop()
    {
        // Streamed newest first, not loaded into a list: FromRuns stops reading once it has 30 trades,
        // so older runs' result JSON is never pulled from the database.
        var runs = _db.BacktestRuns.OrderByDescending(r => r.RunAt).Take(RunsScanned)
            .Select(r => r.ResultJson).AsEnumerable();
        var trades = TypicalStop.FromRuns(runs, Id);
        TypicalStopTrades     = TypicalStop.Recent(trades).Count;
        TypicalStopPoints     = TypicalStop.Median(trades);
        TypicalPositionPoints = TypicalStop.MedianPosition(trades);
    }

    private void Validate(BasketEntry e)
    {
        var c = e.Config;
        if (string.IsNullOrWhiteSpace(e.Ticker)) Errors.Add("Choose an instrument.");
        if (c.Contracts < 1) Errors.Add("Contracts must be at least 1.");
        if (c.AutoSizeByRisk && c.MaxContracts < c.Contracts) Errors.Add("Most contracts can't be fewer than the starting contracts.");
        if (c.StopMode == "OrbPct" && c.StopPct <= 0) Errors.Add("Stop must be more than 0% of the range.");
        if (c.TargetMode == TargetMode.RangePct && c.TargetPct <= 0) Errors.Add("Target must be more than 0% of the range.");
        if (c.UsePartial && (c.PartialPct <= 0 || c.PartialPct >= 100)) Errors.Add("The first target must be between 1% and 99% of the way to the target.");
        if (c.UsePartial && c.PartialCts >= c.Contracts && c.PartialCts > 0) Errors.Add("Contracts at the first target must be fewer than the total.");
        if (c.MaxTrades < 0 || c.MaxLongTrades < 0 || c.MaxShortTrades < 0) Errors.Add("Trade limits can't be negative.");
        if (c.UseCustomOrbWindow && c.OrbEnd <= c.OrbStart) Errors.Add("The opening range has to end after it starts.");
        if (e.AutoTrail is { Enabled: true } t && (t.StopLoss <= 0 || t.Freq <= 0)) Errors.Add("Auto-trail needs a trail distance and a step above 0.");
    }

    /// <summary>What happens to a running engine, in plain words.</summary>
    private string RestartNote(BasketEntry before, BasketEntry after)
    {
        if (!_engine.IsRunning) return "Saved. The engine uses it next time it starts.";
        var structural = before.Ticker != after.Ticker || before.StrategyType != after.StrategyType ||
                         before.ExecutionTFMinutes != after.ExecutionTFMinutes || (!before.Enabled && after.Enabled);
        return structural
            ? "Saved. The engine is running: the instrument, type, bar size or switching it on take effect the next time it starts."
            : "Saved. The running engine uses the new settings for its next trades.";
    }
}
