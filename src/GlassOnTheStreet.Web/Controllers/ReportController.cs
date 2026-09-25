using Microsoft.AspNetCore.Mvc;

namespace GlassOnTheStreet.Web.Controllers;

[Route("report")]
public class ReportController : Controller
{
    [HttpGet("")]
    public IActionResult Index()
    {
        return View();
    }

    [HttpGet("confirmation")]
    public IActionResult Confirmation([FromQuery] bool showPoliceLink)
    {
        ViewBag.ShowPoliceLink = showPoliceLink;
        return View();
    }
}
