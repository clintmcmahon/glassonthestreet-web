using GlassOnTheStreet.Web.Data;
using GlassOnTheStreet.Web.Models;
using Microsoft.EntityFrameworkCore;

namespace GlassOnTheStreet.Web.Services;

/// <summary>
/// Resident reports get the same pin MPD gives that block (MPD places every
/// record at the midpoint of its block), so both sources stack on one point
/// instead of drifting apart:
///
/// 1. Reverse geocode the point to a street and house number, work out the
///    MPD block label ("0048XX 13TH AVE S") and reuse the anchor already on
///    file for that block from the MPD import. Identical by construction.
/// 2. If MPD has no record for that block (or the street name doesn't
///    match), compute the block midpoint from OpenStreetMap.
/// 3. If that fails too, fall back to a coarse grid, never the precise
///    point and never the old fine grid.
/// </summary>
public class BlockAnchorService(
    GlassOnTheStreetContext db,
    IGeocodingService geocodingService,
    IIntersectionFinder intersectionFinder,
    ILocationPrivacyService privacyService,
    ILogger<BlockAnchorService> logger) : IBlockAnchorService
{
    public async Task<BlockAnchor> SnapAsync(decimal lat, decimal lng, CancellationToken cancellationToken = default)
    {
        var address = await geocodingService.ReverseGeocodeAddressAsync(lat, lng, cancellationToken);

        if (address?.Road is { } road)
        {
            var houseNumber = MpdAddress.ParseHouseNumber(address.HouseNumber);

            if (houseNumber is int number)
            {
                var label = MpdAddress.BlockLabel(number, MpdAddress.NormalizeStreet(road));
                var known = await db.Reports
                    .Where(r => r.SourceType == SourceType.OfficialImport && r.Address == label)
                    .GroupBy(r => new { r.DisplayLat, r.DisplayLng })
                    .Select(g => new { g.Key.DisplayLat, g.Key.DisplayLng, Count = g.Count() })
                    .OrderByDescending(g => g.Count)
                    .FirstOrDefaultAsync(cancellationToken);

                if (known is not null)
                {
                    return new BlockAnchor(known.DisplayLat, known.DisplayLng, "mpd-block");
                }
            }

            var midpoint = await intersectionFinder.FindBlockMidpointAsync(lat, lng, road, cancellationToken);
            if (midpoint is not null)
            {
                return new BlockAnchor(midpoint.Value.Lat, midpoint.Value.Lng, "osm-midpoint");
            }
        }

        logger.LogWarning("No block anchor found for a submission (street {Road}); using the coarse grid", address?.Road);
        var (gridLat, gridLng) = privacyService.SnapToCoarseGrid(lat, lng);
        return new BlockAnchor(gridLat, gridLng, "coarse-grid");
    }
}
