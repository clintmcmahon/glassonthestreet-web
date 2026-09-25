using System.Text.Json;

namespace GlassOnTheStreet.Web.Services;

/// <summary>
/// Verifies Cloudflare Turnstile tokens. If no secret key is configured
/// (e.g. local development), verification is skipped and every submission
/// passes -- set Captcha:TurnstileSecretKey in production.
/// </summary>
public class TurnstileCaptchaService(HttpClient httpClient, IConfiguration configuration) : ICaptchaService
{
    public async Task<bool> VerifyAsync(string? token, string? remoteIp, CancellationToken cancellationToken = default)
    {
        var secretKey = configuration["Captcha:TurnstileSecretKey"];
        if (string.IsNullOrWhiteSpace(secretKey))
        {
            return true;
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
