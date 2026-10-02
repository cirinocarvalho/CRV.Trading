using System.Globalization;
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

    private readonly StrategyBasketService _basket;
    private readonly LiveEngineOrchestrator _engine;

    public StrategyModel(StrategyBasketService basket, LiveEngineOrchestrator engine)
    {
        _basket = basket; _engine = engine;
    }

    [BindProperty(SupportsGet = true)] public string Id { get; set; } = "";

    public BasketEntry Entry { get; private set; } = new();
    public bool IsEmaBasket { get; private set; }
    public bool EngineRunning => _engine.IsRunning;
    public List<string> Errors { get; } = new();
    public List<string> Warnings { get; } = new();
    public string? Saved { get; private set; }
    public string? RemoveError { get; private set; }

    public IActionResult OnGet()
    {
        if (!Load()) return NotFound();
        Saved = TempData["strategy_saved"] as string;
        RemoveError = TempData["strategy_error"] as string;
        if (TempData["strategy_warnings"] is string w) Warnings.AddRange(w.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync()
    {
        if (!Load()) return NotFound();
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
        if (Errors.Count > 0)
        {
            Entry = edited;
            return Page();
        }

        var change = _basket.Replace(Id, edited, Who);
        if (!change.Ok) { Errors.Add(change.Error!); Entry = edited; return Page(); }

        TempData["strategy_saved"] = RestartNote(before, edited);
        if (change.Warnings.Count > 0) TempData["strategy_warnings"] = string.Join("\n", change.Warnings);
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
        IsEmaBasket = item.IsEmaBasket;
        return true;
    }

    private void Validate(BasketEntry e)
    {
        var c = e.Config;
        if (string.IsNullOrWhiteSpace(e.Ticker)) Errors.Add("Choose an instrument.");
        if (c.Contracts < 1) Errors.Add("Contracts must be at least 1.");
        if (c.AutoSizeByRisk && c.MaxContracts < c.Contracts) Errors.Add("Most contracts can't be fewer than the starting contracts.");
        if (c.StopMode == "OrbPct" && c.StopPct <= 0) Errors.Add("Stop must be more than 0% of the range.");
        if (c.TargetPct <= 0) Errors.Add("Target must be more than 0% of the range.");
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
