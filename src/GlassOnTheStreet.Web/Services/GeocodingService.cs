using System.Globalization;
using System.Text.Json;

namespace GlassOnTheStreet.Web.Services;

public class GeocodingService(HttpClient httpClient) : IGeocodingService
{
    // Nominatim's usage policy requires a descriptive User-Agent identifying
    // the application -- set on the named HttpClient in Program.cs.
    private const string ViewBox = "-93.40,45.05,-93.15,44.85";

    public async Task<GeocodeResult?> GeocodeAsync(string query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        var url = "search" +
                   $"?q={Uri.EscapeDataString(query)}" +
                   "&format=jsonv2" +
                   "&limit=1" +
                   $"&viewbox={ViewBox}" +
                   "&bounded=1";

        using var response = await httpClient.GetAsync(url, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var results = await JsonSerializer.DeserializeAsync<JsonElement[]>(stream, cancellationToken: cancellationToken);

        if (results is null || results.Length == 0)
        {
            return null;
        }

        var first = results[0];
        var lat = decimal.Parse(first.GetProperty("lat").GetString()!, CultureInfo.InvariantCulture);
        var lng = decimal.Parse(first.GetProperty("lon").GetString()!, CultureInfo.InvariantCulture);
        var displayName = first.GetProperty("display_name").GetString() ?? query;

        return new GeocodeResult(lat, lng, displayName);
    }
}
