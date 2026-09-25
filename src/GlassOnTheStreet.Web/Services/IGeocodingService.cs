namespace GlassOnTheStreet.Web.Services;

public record GeocodeResult(decimal Lat, decimal Lng, string DisplayName);

public interface IGeocodingService
{
    Task<GeocodeResult?> GeocodeAsync(string query, CancellationToken cancellationToken = default);
}
