namespace GlassOnTheStreet.Web.Services;

public interface IGeofenceService
{
    /// <summary>
    /// Returns true if the coordinate falls within the Minneapolis metro
    /// bounding box. Reports outside this area are rejected server-side.
    /// </summary>
    bool IsWithinServiceArea(decimal lat, decimal lng);
}
