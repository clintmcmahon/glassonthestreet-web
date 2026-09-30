using GlassOnTheStreet.Web.Services;
using Xunit;

namespace GlassOnTheStreet.Tests;

public class MpdAddressTests
{
    [Theory]
    [InlineData("13th Avenue South", "13TH AVE S")]
    [InlineData("Malcolm Avenue Southeast", "MALCOLM AVE SE")]
    [InlineData("4th Street North", "4TH ST N")]
    [InlineData("East Minnehaha Parkway", "EAST MINNEHAHA PKWY")]
    [InlineData("West Bde Maka Ska Parkway", "WEST BDE MAKA SKA PKWY")]
    [InlineData("West River Road North", "WEST RIVER RD N")]
    [InlineData("West Broadway", "WEST BROADWAY")]
    [InlineData("East River Terrace", "EAST RIVER TERR")]
    [InlineData("Industrial Boulevard", "INDUSTRIAL BLVD")]
    [InlineData("East 49th Street", "49TH ST E")]
    [InlineData("West 48th Street", "48TH ST W")]
    [InlineData("North 4th Street", "4TH ST N")]
    public void NormalizeStreet_MatchesMpdNaming(string osm, string expected)
    {
        Assert.Equal(expected, MpdAddress.NormalizeStreet(osm));
    }

    [Theory]
    [InlineData(4836, "13TH AVE S", "0048XX 13TH AVE S")]
    [InlineData(4900, "12TH AVE S", "0049XX 12TH AVE S")]
    [InlineData(47, "EMERSON AVE N", "0000XX EMERSON AVE N")]
    public void BlockLabel_UsesTheHundredBlock(int number, string street, string expected)
    {
        Assert.Equal(expected, MpdAddress.BlockLabel(number, street));
    }

    [Theory]
    [InlineData("4836", 4836)]
    [InlineData("4836A", 4836)]
    [InlineData("4836-4838", 4836)]
    [InlineData("rear", null)]
    [InlineData(null, null)]
    public void ParseHouseNumber_ReadsLeadingDigits(string? input, int? expected)
    {
        Assert.Equal(expected, MpdAddress.ParseHouseNumber(input));
    }
}

public class PickBlockMidpointTests
{
    // 13th Ave S runs north-south; 48th St E and 49th St E cross it (real
    // OSM positions), and MPD's anchor for the 4800 block sits between them.
    private static readonly OsmWay Avenue = new("13th Avenue South",
    [
        new OsmNode(1, 44.9161, -93.256241),
        new OsmNode(2, 44.91429, -93.256238),
        new OsmNode(3, 44.91227, -93.256235),
    ]);

    private static readonly OsmWay Street48 = new("East 48th Street",
    [
        new OsmNode(1, 44.9161, -93.256241),
        new OsmNode(10, 44.9161, -93.2540),
    ]);

    private static readonly OsmWay Street49 = new("East 49th Street",
    [
        new OsmNode(2, 44.91429, -93.256238),
        new OsmNode(20, 44.91429, -93.2540),
    ]);

    private static readonly OsmWay Street50 = new("East 50th Street",
    [
        new OsmNode(3, 44.91227, -93.256235),
        new OsmNode(30, 44.91227, -93.2540),
    ]);

    [Theory]
    [InlineData(44.9152)] // 4836, nearer 48th
    [InlineData(44.9146)] // 4870, nearer 49th: still the 48th to 49th block
    public void ReturnsTheMidpointBetweenTheCrossStreetsOnEitherSide(double pointLat)
    {
        var pick = OverpassIntersectionFinder.PickBlockMidpoint([Avenue, Street48, Street49, Street50], pointLat, -93.2565, "13th Avenue South");

        Assert.NotNull(pick);
        Assert.Equal(44.915195, pick!.Value.Lat, 5);
        Assert.Equal(-93.2562395, pick.Value.Lng, 5);
    }

    [Fact]
    public void ReturnsTheNextBlockForAPointBetweenTheOtherCrossStreets()
    {
        var pick = OverpassIntersectionFinder.PickBlockMidpoint([Avenue, Street48, Street49, Street50], 44.9132, -93.2565, "13th Avenue South");

        Assert.Equal(44.91328, pick!.Value.Lat, 5);
    }

    [Fact]
    public void ReturnsNullWhenThePointIsBeyondTheIntersectionsFound()
    {
        Assert.Null(OverpassIntersectionFinder.PickBlockMidpoint([Avenue, Street48, Street49, Street50], 44.9200, -93.2565, "13th Avenue South"));
    }

    [Fact]
    public void ReturnsNullWhenTheStreetIsNotNearby()
    {
        Assert.Null(OverpassIntersectionFinder.PickBlockMidpoint([Street48], 44.9152, -93.2565, "13th Avenue South"));
    }

    [Fact]
    public void IgnoresJoinsBetweenSegmentsOfTheSameStreet()
    {
        var north = new OsmWay("13th Avenue South", [new OsmNode(5, 44.92, -93.2563), new OsmNode(6, 44.919, -93.2563)]);
        var south = new OsmWay("13th Avenue South", [new OsmNode(6, 44.919, -93.2563), new OsmNode(7, 44.918, -93.2563)]);

        Assert.Null(OverpassIntersectionFinder.PickBlockMidpoint([north, south], 44.9185, -93.2565, "13th Avenue South"));
    }
}
