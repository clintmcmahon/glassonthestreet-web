using System.Text.Json;

namespace GlassOnTheStreet.Web.Services;

/// <summary>
/// Reads one page of a City of Minneapolis (ArcGIS) feature service query, retrying a few
/// times. The public service answers 504 or an embedded {"error": ...} body fairly often, so a
/// single failed page must never be mistaken for the end of the data: callers treat a null
/// result as a failure and stop without marking anything complete.
/// </summary>
public static class ArcGisPageFetcher
{
    public const int DefaultAttempts = 4;

    public static async Task<JsonElement?> GetPageAsync(
        HttpClient httpClient, string url, ILogger logger, CancellationToken cancellationToken,
        int attempts = DefaultAttempts, TimeSpan? delayPerAttempt = null)
    {
        var delay = delayPerAttempt ?? TimeSpan.FromSeconds(2);

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                using var response = await httpClient.GetAsync(url, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                    var doc = await JsonSerializer.DeserializeAsync<JsonElement>(stream, cancellationToken: cancellationToken);
                    if (!doc.TryGetProperty("error", out _) && doc.TryGetProperty("features", out var features) && features.ValueKind == JsonValueKind.Array)
                    {
                        return doc;
                    }
                }

                logger.LogWarning("MPD feed page failed (status {Status}), attempt {Attempt} of {Attempts}", (int)response.StatusCode, attempt, attempts);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "MPD feed page request failed, attempt {Attempt} of {Attempts}", attempt, attempts);
            }

            if (attempt < attempts)
            {
                await Task.Delay(delay * attempt, cancellationToken);
            }
        }

        return null;
    }
}
