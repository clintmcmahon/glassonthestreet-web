using System.Globalization;
using System.Text.Json;

namespace GlassOnTheStreet.Web.Services;

public record OsmNode(long Id, double Lat, double Lng);

public record OsmWay(string Name, IReadOnlyList<OsmNode> Nodes);

public class OverpassIntersectionFinder(HttpClient httpClient, ILogger<OverpassIntersectionFinder> logger) : IIntersectionFinder
{
    private const int SearchRadiusMeters = 250;

    private const string Endpoint = "https://overpass-api.de/api/interpreter";
    private const int Attempts = 3;

    public async Task<(decimal Lat, decimal Lng)?> FindBlockMidpointAsync(
        decimal lat, decimal lng, string road, CancellationToken cancellationToken = default)
    {
        var latText = lat.ToString(CultureInfo.InvariantCulture);
        var lngText = lng.ToString(CultureInfo.InvariantCulture);
        var query = "[out:json][timeout:15];" +
                    $"way(around:{SearchRadiusMeters},{latText},{lngText})[\"highway\"][\"name\"]" +
                    "[\"highway\"!~\"^(footway|cycleway|path|steps|service|pedestrian|track|corridor)$\"];" +
                    "out geom;";

        try
        {
            var doc = await QueryAsync(query, cancellationToken);
            if (doc is null)
            {
                return null;
            }

            var ways = Parse(doc.Value);
            var picked = PickBlockMidpoint(ways, (double)lat, (double)lng, road);
            return picked is null ? null : ((decimal)Math.Round(picked.Value.Lat, 6), (decimal)Math.Round(picked.Value.Lng, 6));
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Overpass intersection lookup returned unexpected data");
            return null;
        }
    }

    // The public Overpass server answers 504 roughly half the time when
    // busy, so retry a couple of times before giving up.
    private async Task<JsonElement?> QueryAsync(string query, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            try
            {
                using var response = await httpClient.PostAsync(
                    Endpoint,
                    new FormUrlEncodedContent(new Dictionary<string, string> { ["data"] = query }),
                    cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                    return await JsonSerializer.DeserializeAsync<JsonElement>(stream, cancellationToken: cancellationToken);
                }

                logger.LogWarning("Overpass returned {Status} (attempt {Attempt})", (int)response.StatusCode, attempt + 1);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Overpass request failed (attempt {Attempt})", attempt + 1);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        }

        return null;
    }

    private static List<OsmWay> Parse(JsonElement doc)
    {
        var ways = new List<OsmWay>();
        if (!doc.TryGetProperty("elements", out var elements))
        {
            return ways;
        }

        foreach (var element in elements.EnumerateArray())
        {
            if (element.GetProperty("type").GetString() != "way"
                || !element.TryGetProperty("tags", out var tags)
                || !tags.TryGetProperty("name", out var name)
                || !element.TryGetProperty("nodes", out var nodeIds)
                || !element.TryGetProperty("geometry", out var geometry))
            {
                continue;
            }

            var ids = nodeIds.EnumerateArray().Select(n => n.GetInt64()).ToList();
            var points = geometry.EnumerateArray().ToList();
            if (ids.Count != points.Count)
            {
                continue;
            }

            var nodes = ids.Select((id, i) => new OsmNode(id, points[i].GetProperty("lat").GetDouble(), points[i].GetProperty("lon").GetDouble())).ToList();
            ways.Add(new OsmWay(name.GetString()!, nodes));
        }

        return ways;
    }

    /// <summary>
    /// Finds the cross streets on either side of the point along the named
    /// road and returns the midpoint between them, matching how MPD anchors a
    /// block. Order along the road comes from the axis between its two
    /// farthest intersections, so this suits the near-straight street grid;
    /// a point outside the intersections found returns null.
    /// </summary>
    public static (double Lat, double Lng)? PickBlockMidpoint(
        IReadOnlyList<OsmWay> ways, double lat, double lng, string road)
    {
        var roadWays = ways.Where(w => string.Equals(w.Name, road, StringComparison.OrdinalIgnoreCase)).ToList();
        if (roadWays.Count == 0)
        {
            return null;
        }

        var namesAtNode = new Dictionary<long, HashSet<string>>();
        foreach (var way in ways)
        {
            foreach (var node in way.Nodes)
            {
                if (!namesAtNode.TryGetValue(node.Id, out var names))
                {
                    namesAtNode[node.Id] = names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                }

                names.Add(way.Name);
            }
        }

        var intersections = roadWays
            .SelectMany(w => w.Nodes)
            .DistinctBy(n => n.Id)
            .Where(n => namesAtNode[n.Id].Any(name => !name.Equals(road, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (intersections.Count < 2)
        {
            return null;
        }

        // Axis of the road: from the intersection at one extreme to the other.
        var (first, last) = FarthestPair(intersections);
        var cosLat = Math.Cos(lat * Math.PI / 180);
        var axisX = (last.Lng - first.Lng) * cosLat;
        var axisY = last.Lat - first.Lat;
        var axisLength = Math.Sqrt(axisX * axisX + axisY * axisY);

        double Along(double pointLat, double pointLng) =>
            (((pointLng - first.Lng) * cosLat * axisX) + ((pointLat - first.Lat) * axisY)) / axisLength;

        var here = Along(lat, lng);
        var before = intersections.Where(n => Along(n.Lat, n.Lng) <= here).MaxBy(n => Along(n.Lat, n.Lng));
        var after = intersections.Where(n => Along(n.Lat, n.Lng) > here).MinBy(n => Along(n.Lat, n.Lng));
        if (before is null || after is null)
        {
            return null;
        }

        return ((before.Lat + after.Lat) / 2, (before.Lng + after.Lng) / 2);
    }

    private static (OsmNode A, OsmNode B) FarthestPair(IReadOnlyList<OsmNode> nodes)
    {
        var best = (A: nodes[0], B: nodes[1], Distance: -1.0);
        for (var i = 0; i < nodes.Count; i++)
        {
            for (var j = i + 1; j < nodes.Count; j++)
            {
                var distance = DistanceMeters(nodes[i].Lat, nodes[i].Lng, nodes[j].Lat, nodes[j].Lng);
                if (distance > best.Distance)
                {
                    best = (nodes[i], nodes[j], distance);
                }
            }
        }

        return (best.A, best.B);
    }

    private static double DistanceMeters(double lat1, double lng1, double lat2, double lng2)
    {
        var dy = (lat2 - lat1) * 111_320;
        var dx = (lng2 - lng1) * 111_320 * Math.Cos(lat1 * Math.PI / 180);
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
