using GlassOnTheStreet.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace GlassOnTheStreet.Web.Controllers.Api;

/// <summary>The 13 council wards: official boundaries joined with each ward's council member.</summary>
[ApiController]
[Route("api/wards")]
public class WardsApiController(IWardService wards) : ControllerBase
{
    [HttpGet]
    [ResponseCache(Duration = 86400, Location = ResponseCacheLocation.Client)]
    public IActionResult Get() => Ok(new
    {
        type = "FeatureCollection",
        meta = new { source = wards.Source, retrievedOn = wards.RetrievedOn },
        features = wards.All.Select(w => new
        {
            type = "Feature",
            geometry = w.Geometry,
            properties = new
            {
                ward = w.Ward, name = w.Name, title = w.Title, phone = w.Phone,
                pageUrl = w.PageUrl, contactUrl = w.ContactUrl, statsUrl = w.StatsUrl,
                labelLng = w.LabelLng, labelLat = w.LabelLat
            }
        })
    });
}
