namespace GlassOnTheStreet.Web.Services;

public class GeofenceService : IGeofenceService
{
    // Generous bounding box covering Minneapolis and its immediate inner-ring
    // suburbs. Intentionally coarse (a box, not a precise city boundary) --
    // good enough to reject obviously out-of-area submissions without
    // rejecting a real report near the edge of the city. The city's north
    // edge reaches about 45.0513 (MPD records sit at 45.0502 on Aldrich Ave N),
    // so the box has to extend past 45.05.
    private const decimal MinLat = 44.85m;
    private const decimal MaxLat = 45.07m;
    private const decimal MinLng = -93.40m;
    private const decimal MaxLng = -93.15m;

    public bool IsWithinServiceArea(decimal lat, decimal lng)
    {
        return lat >= MinLat && lat <= MaxLat && lng >= MinLng && lng <= MaxLng;
    }
}
