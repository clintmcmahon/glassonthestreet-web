namespace GlassOnTheStreet.Web.Services;

public interface IIntersectionFinder
{
    /// <summary>
    /// Finds the midpoint of the block containing the point (halfway between
    /// the two cross streets on either side of it), from OpenStreetMap
    /// street data. Null when the lookup fails or no street matching
    /// <paramref name="road"/> is nearby.
    /// </summary>
    Task<(decimal Lat, decimal Lng)?> FindBlockMidpointAsync(
        decimal lat, decimal lng, string road, CancellationToken cancellationToken = default);
}
