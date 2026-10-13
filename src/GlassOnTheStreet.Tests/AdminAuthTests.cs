using System.Net;
using System.Text;
using GlassOnTheStreet.Web.Data;
using GlassOnTheStreet.Web.Infrastructure;
using GlassOnTheStreet.Web.Models;
using GlassOnTheStreet.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace GlassOnTheStreet.Tests;

public class AdminAuthTests
{
    private readonly IMemoryCache cache = new MemoryCache(new MemoryCacheOptions());
    private readonly string dbName = Guid.NewGuid().ToString();

    private static readonly string Hash = AdminPasswordHasher.Hash("correct horse battery");

    private GlassOnTheStreetContext NewDb() =>
        new(new DbContextOptionsBuilder<GlassOnTheStreetContext>().UseInMemoryDatabase(dbName).Options);

    private static void Seed(GlassOnTheStreetContext db)
    {
        db.AdminUsers.Add(new AdminUser { Username = "admin", PasswordHash = Hash });
        db.SaveChanges();
    }

    private bool nextCalled;

    private ActionExecutingContext Call(string? basic, string ip = "203.0.113.5", bool seeded = true)
    {
        var db = NewDb();
        if (seeded && !db.AdminUsers.Any())
        {
            Seed(db);
        }

        var services = new ServiceCollection().AddSingleton(db).AddSingleton(cache).BuildServiceProvider();
        var http = new DefaultHttpContext { RequestServices = services };
        http.Connection.RemoteIpAddress = IPAddress.Parse(ip);
        if (basic is not null)
        {
            http.Request.Headers.Authorization = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(basic));
        }

        var actionContext = new ActionContext(http, new RouteData(), new ActionDescriptor());
        var context = new ActionExecutingContext(actionContext, new List<IFilterMetadata>(), new Dictionary<string, object?>(), new object());
        nextCalled = false;
        new AdminBasicAuthAttribute().OnActionExecutionAsync(context, () =>
        {
            nextCalled = true;
            return Task.FromResult(new ActionExecutedContext(actionContext, new List<IFilterMetadata>(), new object()));
        }).GetAwaiter().GetResult();
        return context;
    }

    [Fact]
    public void CorrectCredentialsPass()
    {
        var context = Call("admin:correct horse battery");

        Assert.Null(context.Result);
        Assert.True(nextCalled);
        Assert.Equal("admin", context.HttpContext.Items[AdminBasicAuthAttribute.UserItem]);
    }

    [Fact]
    public void WrongPasswordIsChallenged() => Assert.IsType<UnauthorizedResult>(Call("admin:wrong").Result);

    [Fact]
    public void WrongUsernameWithTheRightPasswordIsChallenged() => Assert.IsType<UnauthorizedResult>(Call("root:correct horse battery").Result);

    [Fact]
    public void NoAdminInTheDatabaseFailsClosed()
    {
        var result = Call("admin:correct horse battery", seeded: false).Result;

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, Assert.IsType<StatusCodeResult>(result).StatusCode);
    }

    [Fact]
    public void AdminResponsesAreNeverCached()
    {
        var context = Call(null);

        Assert.Equal("no-store", context.HttpContext.Response.Headers.CacheControl.ToString());
        Assert.Contains("noindex", context.HttpContext.Response.Headers["X-Robots-Tag"].ToString());
    }

    [Fact]
    public void RepeatedWrongGuessesLockTheAddressOutEvenForTheRightPassword()
    {
        for (var i = 0; i < AdminBasicAuthAttribute.MaxFailures; i++)
        {
            Assert.IsType<UnauthorizedResult>(Call("admin:guess" + i).Result);
        }

        var locked = Call("admin:correct horse battery");

        Assert.Equal(StatusCodes.Status429TooManyRequests, Assert.IsType<StatusCodeResult>(locked.Result).StatusCode);
        Assert.NotEmpty(locked.HttpContext.Response.Headers.RetryAfter.ToString());
    }

    [Fact]
    public void ALockoutOnlyAppliesToTheAddressThatGuessed()
    {
        for (var i = 0; i < AdminBasicAuthAttribute.MaxFailures; i++)
        {
            Call("admin:guess" + i);
        }

        Assert.Null(Call("admin:correct horse battery", ip: "198.51.100.9").Result);
    }

    [Fact]
    public void ABrowsersFirstVisitWithNoCredentialsIsNotCountedAsAGuess()
    {
        for (var i = 0; i < AdminBasicAuthAttribute.MaxFailures + 5; i++)
        {
            Assert.IsType<UnauthorizedResult>(Call(null).Result);
        }

        Assert.Null(Call("admin:correct horse battery").Result);
    }

    [Fact]
    public void ASuccessfulLoginClearsEarlierFailures()
    {
        for (var i = 0; i < AdminBasicAuthAttribute.MaxFailures - 1; i++)
        {
            Call("admin:guess" + i);
        }

        Assert.Null(Call("admin:correct horse battery").Result);
        Assert.IsType<UnauthorizedResult>(Call("admin:guess").Result);
        Assert.Null(Call("admin:correct horse battery").Result);
    }

    [Fact]
    public void AnUnknownUsernameIsChallengedLikeAWrongPassword() => Assert.IsType<UnauthorizedResult>(Call("nobody:correct horse battery").Result);
}

public class AdminPasswordHasherTests
{
    [Fact]
    public void AHashVerifiesOnlyItsOwnPassword()
    {
        var hash = AdminPasswordHasher.Hash("a long enough passphrase");

        Assert.True(AdminPasswordHasher.Verify("a long enough passphrase", hash));
        Assert.False(AdminPasswordHasher.Verify("a long enough passphrasE", hash));
    }

    [Fact]
    public void TheSamePasswordHashesDifferentlyEachTime() =>
        Assert.NotEqual(AdminPasswordHasher.Hash("same password here"), AdminPasswordHasher.Hash("same password here"));

    [Fact]
    public void TheStoredFormNeverContainsThePassword() =>
        Assert.DoesNotContain("hunter2hunter2hunter2", AdminPasswordHasher.Hash("hunter2hunter2hunter2"));

    [Theory]
    [InlineData("")]
    [InlineData("plain text")]
    [InlineData("v1.abc.def.ghi")]
    [InlineData("v1.1.!!!.!!!")]
    public void MalformedStoredValuesNeverVerify(string stored) => Assert.False(AdminPasswordHasher.Verify("anything", stored));
}
