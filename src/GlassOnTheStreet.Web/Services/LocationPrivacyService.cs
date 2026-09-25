namespace GlassOnTheStreet.Web.Services;

public class LocationPrivacyService : ILocationPrivacyService
{
    // ~0.0018 degrees of latitude is roughly 200 meters in the Twin Cities,
    // which lands a snapped point on the block rather than a specific house.
    private const decimal GridSize = 0.0018m;

    public (decimal Lat, decimal Lng) SnapToBlock(decimal lat, decimal lng)
    {
        var snappedLat = SnapToGrid(lat);
        var snappedLng = SnapToGrid(lng);
        return (snappedLat, snappedLng);
    }

    private static decimal SnapToGrid(decimal value)
    {
        var steps = Math.Round(value / GridSize, MidpointRounding.AwayFromZero);
        return Math.Round(steps * GridSize, 6);
    }
}
