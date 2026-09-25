namespace GlassOnTheStreet.Web.Services;

public interface ILocationPrivacyService
{
    /// <summary>
    /// Snaps a precise coordinate down to a coarse grid (roughly block-level)
    /// so the publicly displayed point never reveals which house was hit.
    /// </summary>
    (decimal Lat, decimal Lng) SnapToBlock(decimal lat, decimal lng);
}
