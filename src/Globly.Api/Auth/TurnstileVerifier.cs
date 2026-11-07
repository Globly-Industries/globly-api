using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace Globly.Api.Auth;

public class TurnstileOptions
{
    public const string Section = "Turnstile";

    /// <summary>Public site key, handed to the app via GET /api/auth/config.</summary>
    public string SiteKey { get; set; } = "";

    /// <summary>Secret key for server-side verification. Empty = human check disabled.</summary>
    public string SecretKey { get; set; } = "";

    public bool Enabled => !string.IsNullOrWhiteSpace(SiteKey) && !string.IsNullOrWhiteSpace(SecretKey);
}

/// <summary>Cloudflare Turnstile "are you human" check used at registration.</summary>
public class TurnstileVerifier(HttpClient http, IOptions<TurnstileOptions> options, ILogger<TurnstileVerifier> logger)
{
    private const string VerifyUrl = "https://challenges.cloudflare.com/turnstile/v0/siteverify";

    public bool Enabled => options.Value.Enabled;

    public async Task<bool> VerifyAsync(string? token, string? remoteIp, CancellationToken ct = default)
    {
        if (!Enabled) return true;
        if (string.IsNullOrWhiteSpace(token)) return false;

        var form = new Dictionary<string, string>
        {
            ["secret"] = options.Value.SecretKey,
            ["response"] = token,
        };
        if (remoteIp is not null) form["remoteip"] = remoteIp;

        try
        {
            using var response = await http.PostAsync(VerifyUrl, new FormUrlEncodedContent(form), ct);
            var result = await response.Content.ReadFromJsonAsync<SiteVerifyResponse>(ct);
            if (result?.Success == true) return true;

            logger.LogInformation("Turnstile rejected a token: {Errors}", string.Join(",", result?.ErrorCodes ?? []));
            return false;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Fail closed: if Cloudflare can't be reached, registration is refused rather than unprotected.
            logger.LogWarning(ex, "Turnstile verification request failed");
            return false;
        }
    }

    private sealed record SiteVerifyResponse(
        [property: JsonPropertyName("success")] bool Success,
        [property: JsonPropertyName("error-codes")] string[]? ErrorCodes);
}
