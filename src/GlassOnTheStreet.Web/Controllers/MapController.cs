using Microsoft.AspNetCore.Mvc;

namespace GlassOnTheStreet.Web.Controllers;

[Route("map")]
public class MapController : Controller
{
    [HttpGet("")]
    public IActionResult Index()
    {
        return View();
    }
}
