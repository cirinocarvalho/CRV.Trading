using CRV.Core.Security;
using Xunit;

namespace CRV.Core.Tests.Security;

public class WebhookAuthTests
{
    private const string Good = "s3cret-value-long-enough";

    // ── Fail-closed behaviour ────────────────────────────────────
    // These are the cases that decide whether an unconfigured deployment
    // leaves a live-order endpoint open to the internet.

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("CHANGE_ME")]
    [InlineData("  CHANGE_ME  ")]
    [InlineData("tooshort")]                 // under MinSecretLength
    public void NotConfigured_WhenServerSecretUnusable(string? configured)
    {
        // Even a caller presenting the "right" string must be refused.
        Assert.Equal(WebhookAuthResult.NotConfigured, WebhookAuth.Validate(configured, configured));
        Assert.Equal(WebhookAuthResult.NotConfigured, WebhookAuth.Validate(configured, Good));
    }

    [Fact]
    public void NotConfigured_TakesPrecedenceOverMissingCaller()
    {
        // Ordering matters: an unconfigured server must not report "Missing",
        // which would read as a caller problem and hide the misconfiguration.
        Assert.Equal(WebhookAuthResult.NotConfigured, WebhookAuth.Validate(null, null));
    }

    // ── Caller validation ────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Missing_WhenCallerPresentsNothing(string? presented)
        => Assert.Equal(WebhookAuthResult.Missing, WebhookAuth.Validate(Good, presented));

    [Theory]
    [InlineData("wrong-secret-long-enough")]
    [InlineData("s3cret-value-long-enoug")]   // one char short — prefix of the real secret
    [InlineData("s3cret-value-long-enoughX")] // real secret plus a char
    [InlineData("S3CRET-VALUE-LONG-ENOUGH")] // case differs
    public void Mismatch_WhenCallerPresentsWrongSecret(string presented)
        => Assert.Equal(WebhookAuthResult.Mismatch, WebhookAuth.Validate(Good, presented));

    [Fact]
    public void Ok_WhenSecretsMatch()
        => Assert.Equal(WebhookAuthResult.Ok, WebhookAuth.Validate(Good, Good));

    [Fact]
    public void Ok_IgnoresSurroundingWhitespace()
    {
        // TradingView alert bodies and shell pipelines routinely add a trailing
        // newline; that must not lock a correctly-configured caller out.
        Assert.Equal(WebhookAuthResult.Ok, WebhookAuth.Validate(Good, $"  {Good}\n"));
        Assert.Equal(WebhookAuthResult.Ok, WebhookAuth.Validate($" {Good} ", Good));
    }

    [Fact]
    public void Ok_ForLongSecretsWithSharedPrefix()
    {
        // Guards the hash-then-compare path: equal-length inputs differing only
        // in the final byte must still be distinguished correctly.
        var a = new string('a', 64);
        Assert.Equal(WebhookAuthResult.Ok, WebhookAuth.Validate(a, a));
        Assert.Equal(WebhookAuthResult.Mismatch, WebhookAuth.Validate(a, a[..63] + "b"));
    }
}
