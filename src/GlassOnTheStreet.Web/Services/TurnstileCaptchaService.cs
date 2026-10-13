using System.Text.Json;

namespace GlassOnTheStreet.Web.Services;

/// <summary>
/// Verifies Cloudflare Turnstile tokens. With no secret key configured,
/// verification is skipped only in Development. Anywhere else a missing key
/// rejects every submission, so a lost setting can't quietly turn spam
/// protection off -- set Captcha:TurnstileSecretKey in production.
/// </summary>
public class TurnstileCaptchaService(HttpClient httpClient, IConfiguration configuration, IHostEnvironment environment) : ICaptchaService
{
    public async Task<bool> VerifyAsync(string? token, string? remoteIp, CancellationToken cancellationToken = default)
    {
        var secretKey = configuration["Captcha:TurnstileSecretKey"];
        if (string.IsNullOrWhiteSpace(secretKey))
        {
            return environment.IsDevelopment();
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["secret"] = secretKey,
            ["response"] = token,
            ["remoteip"] = remoteIp ?? string.Empty
        });

        using var response = await httpClient.PostAsync(
            "https://challenges.cloudflare.com/turnstile/v0/siteverify", form, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            return false;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var result = await JsonSerializer.DeserializeAsync<JsonElement>(stream, cancellationToken: cancellationToken);
        return result.TryGetProperty("success", out var success) && success.GetBoolean();
    }
}
