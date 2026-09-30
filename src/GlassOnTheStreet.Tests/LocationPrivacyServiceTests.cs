using GlassOnTheStreet.Web.Services;
using Xunit;

namespace GlassOnTheStreet.Tests;

public class LocationPrivacyServiceTests
{
    private readonly LocationPrivacyService _service = new();

    [Fact]
    public void SnapToCoarseGrid_MovesPointOffItsExactValue()
    {
        var (lat, lng) = _service.SnapToCoarseGrid(44.97853m, -93.27234m);

        Assert.NotEqual(44.97853m, lat);
        Assert.NotEqual(-93.27234m, lng);
    }

    [Fact]
    public void SnapToCoarseGrid_IsConsistentForNearbyPoints()
    {
        var a = _service.SnapToCoarseGrid(44.97853m, -93.27234m);
        var b = _service.SnapToCoarseGrid(44.97861m, -93.27228m);

        Assert.Equal(a, b);
    }

    [Fact]
    public void SnapToCoarseGrid_IsDeterministic()
    {
        var first = _service.SnapToCoarseGrid(44.9483m, -93.2011m);
        var second = _service.SnapToCoarseGrid(44.9483m, -93.2011m);

        Assert.Equal(first, second);
    }

    [Theory]
    [InlineData(44.9483, -93.2011)]
    [InlineData(45.0, -93.3)]
    [InlineData(44.85, -93.15)]
    public void SnapToCoarseGrid_StaysWithinHalfAGridCellOfTheOriginal(double lat, double lng)
    {
        var (snappedLat, snappedLng) = _service.SnapToCoarseGrid((decimal)lat, (decimal)lng);

        Assert.True(Math.Abs(snappedLat - (decimal)lat) <= 0.0025m);
        Assert.True(Math.Abs(snappedLng - (decimal)lng) <= 0.0025m);
    }
}
