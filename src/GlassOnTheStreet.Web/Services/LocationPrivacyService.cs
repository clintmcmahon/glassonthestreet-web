namespace GlassOnTheStreet.Web.Services;

public class LocationPrivacyService : ILocationPrivacyService
{
    // 0.005 degrees of latitude is roughly 550 meters. Deliberately much
    // coarser than a block: this only applies when the block's midpoint
    // couldn't be determined, and a failure must never leak a near-exact
    // location.
    private const decimal GridSize = 0.005m;

    public (decimal Lat, decimal Lng) SnapToCoarseGrid(decimal lat, decimal lng)
    {
        return (SnapToGrid(lat), SnapToGrid(lng));
    }

    private static decimal SnapToGrid(decimal value)
    {
        var steps = Math.Round(value / GridSize, MidpointRounding.AwayFromZero);
        return Math.Round(steps * GridSize, 6);
    }
}
