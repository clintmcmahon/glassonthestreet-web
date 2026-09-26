using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using GlassOnTheStreet.Web.Models;
using GlassOnTheStreet.Web.Services;

namespace GlassOnTheStreet.Web.Controllers;

public class HomeController(IReportStatsService statsService) : Controller
{
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        // Server-rendered so the real, current count is in the initial HTML
        // -- a crawler or agent that doesn't execute JavaScript should see
        // today's number, not an empty placeholder, and it's accurate as
        // of each request without needing a client-side re-fetch.
        var stats = await statsService.GetStatsAsync(from: null, to: null, cancellationToken);
        var reportingGap = await statsService.GetPoliceReportingGapAsync(cancellationToken);
        return View(new HomePageViewModel(stats, reportingGap));
    }

    [HttpGet("privacy")]
    public IActionResult Privacy()
    {
        return View();
    }

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }
}

public record HomePageViewModel(ReportStats Stats, PoliceReportingGap ReportingGap);
