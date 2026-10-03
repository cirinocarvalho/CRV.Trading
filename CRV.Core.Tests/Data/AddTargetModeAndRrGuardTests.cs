using CRV.Core.Migrations;
using CRV.Core.Models;
using Microsoft.Data.Sqlite;
using Xunit;

namespace CRV.Core.Tests.Data;

/// <summary>
/// The migration writes TargetMode = RangePct, the guard on and Skip into every stored basket
/// entry that has a Config object and doesn't say otherwise, and leaves anything it can't
/// safely edit exactly as it was.
/// </summary>
public class AddTargetModeAndRrGuardTests : IDisposable
{
    private readonly SqliteConnection _conn = new("DataSource=:memory:");

    public AddTargetModeAndRrGuardTests()
    {
        _conn.Open();
        Exec("""CREATE TABLE "Configs" ("Id" INTEGER PRIMARY KEY, "BasketJson" TEXT NULL);""");
    }

    public void Dispose() => _conn.Dispose();

    private void Exec(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private void Insert(int id, string? json)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """INSERT INTO "Configs" ("Id", "BasketJson") VALUES ($id, $json);""";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$json", (object?)json ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private string? Read(int id)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """SELECT "BasketJson" FROM "Configs" WHERE "Id" = $id;""";
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteScalar() as string;
    }

    [Fact]
    public void WritesTheDefaults_KeepsWhatWasSet_AndTouchesNothingElse()
    {
        Insert(1, """[{"Id":"pb","Enabled":true,"Config":{"StopPct":0.10,"MinRr":1.50,"OrbStart":"09:30:00"},"Sessions":[{"SessionId":"NY","Enabled":true}]},{"Id":"of","Config":{"EnforceMinRr":false,"MinRrAction":1}},{"Id":"bare"}]""");

        Exec(AddTargetModeAndRrGuard.Sql);

        var json = Read(1)!;
        Assert.Contains("\"StopPct\":0.10", json);           // numbers keep their text
        Assert.Contains("\"OrbStart\":\"09:30:00\"", json);
        Assert.DoesNotContain("\"Id\":\"bare\",\"Config\"", json);

        var entries = BasketCodec.Parse(json);
        var pb = entries.Single(e => e.Id == "pb").Config;
        Assert.Equal((TargetMode.RangePct, 0m, TargetDollarsBasis.PerContract, true, MinRrAction.Skip),
            (pb.TargetMode, pb.TargetDollars, pb.TargetDollarsBasis, pb.EnforceMinRr, pb.MinRrAction));
        Assert.Contains("\"EnforceMinRr\":true", json);

        var of = entries.Single(e => e.Id == "of").Config;
        Assert.False(of.EnforceMinRr);
        Assert.Equal(MinRrAction.RaiseTarget, of.MinRrAction);
    }

    [Theory]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("not json")]
    [InlineData("""[1,"x"]""")]
    [InlineData("""[{"Id":"lower","config":{"MinRr":2}}]""")]
    public void LeavesWhatItCantSafelyEditUnchanged(string json)
    {
        Insert(2, json);

        Exec(AddTargetModeAndRrGuard.Sql);

        Assert.Equal(json, Read(2));
    }

    [Fact]
    public void LeavesANullBasketNull()
    {
        Insert(3, null);

        Exec(AddTargetModeAndRrGuard.Sql);

        Assert.Null(Read(3));
    }
}
