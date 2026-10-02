using Microsoft.AspNetCore.Mvc;

namespace GlassOnTheStreet.Web.Controllers;

public class NearController : Controller
{
    [HttpGet("near")]
    public IActionResult Index() => View();
}
