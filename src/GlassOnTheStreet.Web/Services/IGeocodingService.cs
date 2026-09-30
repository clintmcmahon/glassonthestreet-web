namespace GlassOnTheStreet.Web.Services;

public record GeocodeResult(decimal Lat, decimal Lng, string DisplayName);

public record ReverseAddress(string? Road, string? HouseNumber);

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

    /// <summary>
    /// Street and house number nearest a point, used to work out which block
    /// a report belongs to. The point is sent to the geocoder and not stored.
    /// Null when the lookup fails.
    /// </summary>
    Task<ReverseAddress?> ReverseGeocodeAddressAsync(decimal lat, decimal lng, CancellationToken cancellationToken = default);
}
