using System.Text;
using Deque.AxeCore.Commons;
using Deque.AxeCore.Playwright;
using Microsoft.Playwright;

namespace CRV.Web.A11yTests;

/// <summary>Runs axe with the WCAG 2.2 A/AA rule set and turns its violations into a failure message.</summary>
public static class AxeReport
{
    public static readonly string[] WcagTags = ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa"];

    public static Task<AxeResult> RunAsync(IPage page, params string[] disabledRules)
    {
        var options = new AxeRunOptions
        {
            RunOnly = new RunOnlyOptions { Type = "tag", Values = WcagTags.ToList() },
            Rules   = disabledRules.ToDictionary(r => r, _ => new RuleOptions { Enabled = false }),
        };
        return page.RunAxe(options);
    }

    public static string Format(string scan, AxeResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{scan}: {result.Violations.Count()} WCAG violation(s)");
        foreach (var v in result.Violations)
        {
            sb.AppendLine($"- {v.Id} [{v.Impact}] {v.Help}");
            sb.AppendLine($"  {v.HelpUrl}");
            foreach (var n in v.Nodes)
            {
                sb.AppendLine($"    at   {n.Target}");
                sb.AppendLine($"    html {n.Html}");
            }
        }
        return sb.ToString();
    }
}
