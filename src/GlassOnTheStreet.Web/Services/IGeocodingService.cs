namespace GlassOnTheStreet.Web.Services;

public record GeocodeResult(decimal Lat, decimal Lng, string DisplayName);

public interface IGeocodingService
{
    Task<GeocodeResult?> GeocodeAsync(string query, CancellationToken cancellationToken = default);

    /// <summary>
    /// Best-effort neighborhood lookup for a point. Returns null if the
    /// lookup fails or no neighborhood-level name is available -- this is
    /// a stats nicety, never something a submission should be rejected
    /// over.
    /// </summary>
    Task<string?> ReverseGeocodeNeighborhoodAsync(decimal lat, decimal lng, CancellationToken cancellationToken = default);
}
