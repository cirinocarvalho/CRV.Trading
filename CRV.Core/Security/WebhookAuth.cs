using System.Security.Cryptography;
using System.Text;

namespace CRV.Core.Security;

/// <summary>Outcome of validating a webhook caller's shared secret.</summary>
public enum WebhookAuthResult
{
    /// <summary>Secret matched — the caller is authorised.</summary>
    Ok,
    /// <summary>No secret is configured server-side. The endpoint must refuse all callers.</summary>
    NotConfigured,
    /// <summary>Caller presented no secret.</summary>
    Missing,
    /// <summary>Caller presented a secret that does not match.</summary>
    Mismatch,
}

/// <summary>
/// Shared-secret authentication for the order webhook.
///
/// The webhook is excluded from Entra Easy Auth (external senders such as
/// TradingView cannot complete an interactive login), so this is the only thing
/// standing between the open internet and a live order. It therefore fails
/// CLOSED: when no secret is configured the endpoint rejects every caller
/// rather than falling back to open access.
/// </summary>
public static class WebhookAuth
{
    /// <summary>Placeholder written by the Bicep template before real secrets are set.</summary>
    public const string Placeholder = "CHANGE_ME";

    /// <summary>Minimum length for a usable secret — shorter values are treated as unconfigured.</summary>
    public const int MinSecretLength = 16;

    /// <summary>
    /// Validate a presented secret against the configured one.
    /// </summary>
    /// <param name="configured">Server-side secret (from <c>Webhook:Secret</c>).</param>
    /// <param name="presented">Secret supplied by the caller (header or body).</param>
    public static WebhookAuthResult Validate(string? configured, string? presented)
    {
        // Fail closed: an unset, placeholder, or trivially short secret is not a
        // secret. Refuse everyone instead of silently accepting anonymous orders.
        if (string.IsNullOrWhiteSpace(configured) ||
            configured.Trim() == Placeholder ||
            configured.Trim().Length < MinSecretLength)
            return WebhookAuthResult.NotConfigured;

        if (string.IsNullOrWhiteSpace(presented))
            return WebhookAuthResult.Missing;

        return FixedTimeEquals(configured.Trim(), presented.Trim())
            ? WebhookAuthResult.Ok
            : WebhookAuthResult.Mismatch;
    }

    /// <summary>
    /// Length-independent, constant-time string comparison. Hashing both sides
    /// first keeps the compared buffers the same size, so neither the length nor
    /// the position of the first differing byte leaks through timing.
    /// </summary>
    private static bool FixedTimeEquals(string a, string b)
    {
        var ha = SHA256.HashData(Encoding.UTF8.GetBytes(a));
        var hb = SHA256.HashData(Encoding.UTF8.GetBytes(b));
        return CryptographicOperations.FixedTimeEquals(ha, hb);
    }
}
