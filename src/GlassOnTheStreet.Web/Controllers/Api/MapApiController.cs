using GlassOnTheStreet.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace GlassOnTheStreet.Web.Controllers.Api;

/// <summary>Data behind the map: one feature per block, and the numbers for the panels below it.</summary>
[ApiController]
[Route("api/map")]
public class MapApiController(MapDataService mapData, IncidentDataCache dataCache) : ControllerBase
{
    private async Task<MapFilter> FilterAsync(string? group, string? range, DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        var dataset = await dataCache.GetAsync(ct);
        var (start, end) = MapDataService.ResolveRange(range, from, to, dataset.Through);
        return new MapFilter(MapDataService.NormalizeGroup(group), start, end);
    }

    [HttpGet("blocks")]
    [ResponseCache(Duration = 120, Location = ResponseCacheLocation.Client)]
    public async Task<IActionResult> Blocks(
        [FromQuery] string? group, [FromQuery] string? range, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to,
        CancellationToken cancellationToken)
    {
        var result = await mapData.GetBlocksAsync(await FilterAsync(group, range, from, to, cancellationToken), cancellationToken);

        return Ok(new
        {
            type = "FeatureCollection",
            meta = new
            {
                from = result.From.ToString("yyyy-MM-dd"),
                to = result.To.ToString("yyyy-MM-dd"),
                through = result.Through.ToString("yyyy-MM-dd"),
                group = result.GroupLabel,
                total = result.Total,
                blocks = result.Blocks.Count,
                max = result.Blocks.Count == 0 ? 0 : result.Blocks[0].Count,
                groupLabels = result.GroupLabels
            },
            features = result.Blocks.Select(b => new
            {
                type = "Feature",
                geometry = new { type = "Point", coordinates = new[] { b.Lng, b.Lat } },
                properties = new { a = b.Address, n = b.Count, h = b.Neighborhood, w = b.Ward, t = b.Top }
            })
        });
    }

    [HttpGet("summary")]
    [ResponseCache(Duration = 120, Location = ResponseCacheLocation.Client)]
    public async Task<IActionResult> Summary(
        [FromQuery] string? group, [FromQuery] string? range, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to,
        CancellationToken cancellationToken)
    {
        var s = await mapData.GetSummaryAsync(await FilterAsync(group, range, from, to, cancellationToken), cancellationToken);

        object Counts(IEnumerable<MapCount> list) => list.Select(c => new { name = c.Name, count = c.Count, url = c.Url });

        return Ok(new
        {
            from = s.From.ToString("yyyy-MM-dd"),
            to = s.To.ToString("yyyy-MM-dd"),
            through = s.Through.ToString("yyyy-MM-dd"),
            group = s.GroupLabel,
            total = s.Total,
            priorTotal = s.PriorTotal,
            changeVsPrior = s.ChangeVsPrior,
            neighborhoods = Counts(s.Neighborhoods),
            wards = Counts(s.Wards),
            timeOfDay = Counts(s.TimeOfDay),
            types = Counts(s.Types)
        });
    }
}
