using System.Text.Json;
using GlassOnTheStreet.Web.Services;

namespace GlassOnTheStreet.Tests;

/// <summary>The ward overlay's data: 13 wards, each with its boundary, its council member and a label point that sits inside it.</summary>
public class WardServiceTests
{
    private readonly WardService service = new();

    /// <summary>Even-odd ray cast. Holes are rings after the first.</summary>
    public static bool Contains(JsonElement geometry, double lng, double lat)
    {
        static bool InRing(JsonElement ring, double x, double y)
        {
            var inside = false;
            var pts = ring.EnumerateArray().Select(p => (X: p[0].GetDouble(), Y: p[1].GetDouble())).ToArray();
            for (int i = 0, j = pts.Length - 1; i < pts.Length; j = i++)
            {
                if ((pts[i].Y > y) != (pts[j].Y > y) && x < (pts[j].X - pts[i].X) * (y - pts[i].Y) / (pts[j].Y - pts[i].Y) + pts[i].X)
                {
                    inside = !inside;
                }
            }

            return inside;
        }

        static bool InPolygon(JsonElement polygon, double x, double y)
        {
            var rings = polygon.EnumerateArray().ToList();
            return InRing(rings[0], x, y) && !rings.Skip(1).Any(h => InRing(h, x, y));
        }

        var coords = geometry.GetProperty("coordinates");
        return geometry.GetProperty("type").GetString() == "MultiPolygon"
            ? coords.EnumerateArray().Any(p => InPolygon(p, lng, lat))
            : InPolygon(coords, lng, lat);
    }

    [Fact]
    public void ThereAreThirteenWardsNumberedOneToThirteen()
    {
        Assert.Equal(Enumerable.Range(1, 13), service.All.Select(w => w.Ward));
        Assert.All(Enumerable.Range(1, 13), n => Assert.NotNull(service.Find(n)));
        Assert.Null(service.Find(0));
        Assert.Null(service.Find(14));
    }

    [Fact]
    public void EveryWardNamesItsCouncilMemberAndOfficePhoneAndLinksToTheCitysPages()
    {
        foreach (var w in service.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(w.Name));
            Assert.Contains(w.Title, new[] { "Council Member", "Council President", "Council Vice President" });
            // The city's council office lines run 612-673-2201 to 2213, one per ward.
            Assert.Equal($"612-673-22{w.Ward:00}", w.Phone);
            Assert.Equal($"https://www.minneapolismn.gov/government/city-council/members/ward-{w.Ward}/", w.PageUrl);
            Assert.Equal($"https://www.minneapolismn.gov/government/city-council/members/ward-{w.Ward}/contact-ward-{w.Ward}/", w.ContactUrl);
            Assert.Equal($"/wards/{w.Ward}", w.StatsUrl);
        }

        Assert.Equal(13, service.All.Select(w => w.Name).Distinct().Count());
        Assert.Single(service.All, w => w.Title == "Council President");
        Assert.Single(service.All, w => w.Title == "Council Vice President");
        Assert.True(DateOnly.TryParse(service.RetrievedOn, out _));
    }

    [Fact]
    public void BoundariesAreClosedRingsInsideMinneapolis()
    {
        foreach (var w in service.All)
        {
            var type = w.Geometry.GetProperty("type").GetString();
            Assert.Contains(type, new[] { "Polygon", "MultiPolygon" });
            var polygons = type == "Polygon" ? [w.Geometry.GetProperty("coordinates")] : w.Geometry.GetProperty("coordinates").EnumerateArray().ToList();

            foreach (var ring in polygons.SelectMany(p => p.EnumerateArray()))
            {
                var pts = ring.EnumerateArray().Select(p => (Lng: p[0].GetDouble(), Lat: p[1].GetDouble())).ToList();
                Assert.True(pts.Count >= 4, $"Ward {w.Ward} has a ring with {pts.Count} points.");
                Assert.Equal(pts[0], pts[^1]);
                Assert.All(pts, p =>
                {
                    Assert.InRange(p.Lat, 44.85, 45.07);
                    Assert.InRange(p.Lng, -93.40, -93.15);
                });
            }
        }
    }

    [Fact]
    public void EachLabelPointIsInsideItsOwnWardAndNoOther()
    {
        foreach (var w in service.All)
        {
            Assert.True(Contains(w.Geometry, w.LabelLng, w.LabelLat), $"Ward {w.Ward}'s label is outside its boundary.");
            foreach (var other in service.All.Where(o => o.Ward != w.Ward))
            {
                Assert.False(Contains(other.Geometry, w.LabelLng, w.LabelLat), $"Ward {w.Ward}'s label is inside ward {other.Ward}.");
            }
        }
    }

    [Fact]
    public void KnownPlacesFallInTheirWards()
    {
        int WardAt(double lng, double lat) => service.All.Single(w => Contains(w.Geometry, lng, lat)).Ward;

        Assert.Equal(7, WardAt(-93.2653, 44.9772)); // City Hall, 350 S 5th St
        Assert.Equal(9, WardAt(-93.2600, 44.9480));
        Assert.Equal(3, WardAt(-93.2630, 44.9895)); // near Nicollet Island
    }
}
