using GlassOnTheStreet.Web.Services;
using Xunit;

namespace GlassOnTheStreet.Tests;

public class GeofenceServiceTests
{
    private readonly GeofenceService _service = new();

    [Theory]
    [InlineData(44.9778, -93.2650)] // downtown Minneapolis
    [InlineData(44.9483, -93.2011)] // Uptown-ish
    [InlineData(45.0, -93.3)]
    public void IsWithinServiceArea_AcceptsMinneapolisMetroCoordinates(double lat, double lng)
    {
        Assert.True(_service.IsWithinServiceArea((decimal)lat, (decimal)lng));
    }

    [Theory]
    [InlineData(41.8781, -87.6298)] // Chicago
    [InlineData(44.9537, -93.0900)] // well east of the bounding box
    [InlineData(0, 0)]
    public void IsWithinServiceArea_RejectsCoordinatesOutsideTheMetro(double lat, double lng)
    {
        Assert.False(_service.IsWithinServiceArea((decimal)lat, (decimal)lng));
    }
}
