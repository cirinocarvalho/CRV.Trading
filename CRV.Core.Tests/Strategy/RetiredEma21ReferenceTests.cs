using Xunit;

namespace CRV.Core.Tests.Strategy;

/// <summary>
/// The EMA21 strategy is retired. No source file outside CRV.Core/Migrations may name it, its
/// basket column, its config method, its basket flag or its five settings. Ema21Indicator,
/// IndicatorState.Ema21 and UseEmaFilter are separate and stay.
/// </summary>
public class RetiredEma21ReferenceTests
{
    // Built from parts so this file doesn't match itself.
    private static readonly string[] Retired =
    {
        "Ema21" + "Strategy", "StrategyType." + "Ema21", "Ema21" + "BasketJson", "ToEma21" + "SetupConfigs", "Is" + "Ema21",
        "Slope" + "Len", "AtrTouch" + "Mult", "MinSlope" + "Pct", "OpenTicks" + "ToEma", "UseVolume" + "Filter",
    };

    private static readonly string[] SkippedDirs = { "bin", "obj", "node_modules", ".git", ".claude" };

    [Fact]
    public void Source_HasNoReferencesToTheRetiredStrategy()
    {
        var root = RepoRoot();
        var migrations = Path.Combine(root, "CRV.Core", "Migrations") + Path.DirectorySeparatorChar;

        var hits = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cs") || f.EndsWith(".cshtml") || f.EndsWith(".js"))
            .Where(f => !f.StartsWith(migrations, StringComparison.Ordinal))
            .Where(f => !Path.GetRelativePath(root, f).Split(Path.DirectorySeparatorChar).Any(SkippedDirs.Contains))
            .SelectMany(f => File.ReadLines(f).Select((line, i) => (File: f, Line: i + 1, Text: line)))
            .Where(x => Retired.Any(name => x.Text.Contains(name, StringComparison.Ordinal)))
            .Select(x => $"{Path.GetRelativePath(root, x.File)}:{x.Line}: {x.Text.Trim()}")
            .ToList();

        Assert.True(hits.Count == 0, "Retired EMA21 names still in source:\n" + string.Join("\n", hits));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "CRV.Trading.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("CRV.Trading.sln not found above " + AppContext.BaseDirectory);
    }
}
