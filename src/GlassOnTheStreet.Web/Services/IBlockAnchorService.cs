namespace GlassOnTheStreet.Web.Services;

/// <param name="Method">How the anchor was found: "mpd-block", "osm-midpoint" or "coarse-grid".</param>
public record BlockAnchor(decimal Lat, decimal Lng, string Method);

public interface IBlockAnchorService
{
    /// <summary>
    /// Snaps a point to the same anchor MPD uses for its own records: the
    /// midpoint of the block. The point given here is never stored; only
    /// the returned anchor is.
    /// </summary>
    Task<BlockAnchor> SnapAsync(decimal lat, decimal lng, CancellationToken cancellationToken = default);
}
