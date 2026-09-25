using GlassOnTheStreet.Web.Services;
using Xunit;

namespace GlassOnTheStreet.Tests;

public class LocationPrivacyServiceTests
{
    private readonly LocationPrivacyService _service = new();

    [Fact]
    public void SnapToBlock_MovesPointOffItsExactValue()
    {
        var (lat, lng) = _service.SnapToBlock(44.97853m, -93.27234m);

        Assert.NotEqual(44.97853m, lat);
        Assert.NotEqual(-93.27234m, lng);
    }

    [Fact]
    public void SnapToBlock_IsConsistentForNearbyPoints()
    {
        // Two addresses on the same block should snap to the same public pin.
        var a = _service.SnapToBlock(44.97853m, -93.27234m);
        var b = _service.SnapToBlock(44.97861m, -93.27228m);

        Assert.Equal(a, b);
    }

    [Fact]
    public void SnapToBlock_IsDeterministic()
    {
        var first = _service.SnapToBlock(44.9483m, -93.2011m);
        var second = _service.SnapToBlock(44.9483m, -93.2011m);

        Assert.Equal(first, second);
    }

    [Theory]
    [InlineData(44.9483, -93.2011)]
    [InlineData(45.0, -93.3)]
    [InlineData(44.85, -93.15)]
    public void SnapToBlock_StaysWithinHalfAGridCellOfTheOriginal(double lat, double lng)
    {
        var (snappedLat, snappedLng) = _service.SnapToBlock((decimal)lat, (decimal)lng);

        Assert.True(Math.Abs(snappedLat - (decimal)lat) <= 0.0009m);
        Assert.True(Math.Abs(snappedLng - (decimal)lng) <= 0.0009m);
    }
}
