namespace GlassOnTheStreet.Web.Services;

public class GeofenceService : IGeofenceService
{
    // Generous bounding box covering Minneapolis and its immediate inner-ring
    // suburbs. Intentionally coarse (a box, not a precise city boundary) --
    // good enough to reject obviously out-of-area submissions without
    // rejecting a real report near the edge of the city.
    private const decimal MinLat = 44.85m;
    private const decimal MaxLat = 45.05m;
    private const decimal MinLng = -93.40m;
    private const decimal MaxLng = -93.15m;

    public bool IsWithinServiceArea(decimal lat, decimal lng)
    {
        return lat >= MinLat && lat <= MaxLat && lng >= MinLng && lng <= MaxLng;
    }
}
