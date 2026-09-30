namespace GlassOnTheStreet.Web.Services;

public interface ILocationPrivacyService
{
    /// <summary>
    /// Last-resort anonymizer: snaps a coordinate to a coarse grid (roughly
    /// 400 to 500 m) when no block anchor can be found. Normal snapping to
    /// the block's midpoint is IBlockAnchorService.
    /// </summary>
    (decimal Lat, decimal Lng) SnapToCoarseGrid(decimal lat, decimal lng);
}
