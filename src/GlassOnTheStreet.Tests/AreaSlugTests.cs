using GlassOnTheStreet.Web.Infrastructure;
using GlassOnTheStreet.Web.Services;
using Xunit;

namespace GlassOnTheStreet.Tests;

public class AreaSlugTests
{
    [Theory]
    [InlineData("Whittier", "whittier")]
    [InlineData("Marcy Holmes", "marcy-holmes")]
    [InlineData("Lowry Hill East", "lowry-hill-east")]
    [InlineData("Cedar-Isles-Dean", "cedar-isles-dean")]
    [InlineData("Windom Park & Waite", "windom-park-and-waite")]
    [InlineData("  St. Anthony East ", "st-anthony-east")]
    public void For_MakesLowercaseUrlSafeSlugs(string name, string expected)
    {
        Assert.Equal(expected, AreaSlug.For(name));
    }
}

public class StructuredDataTests
{
    [Fact]
    public void Tag_WritesThePlusInTheTypeLiterally()
    {
        Assert.StartsWith("<script type=\"application/ld+json\">", StructuredData.Tag("{}"));
    }

    [Fact]
    public void Breadcrumbs_EscapesNamesInsteadOfBreakingTheJson()
    {
        var json = StructuredData.Breadcrumbs("https://example.com", ("Home", "/"), ("O'Brien \"Park\" </script>", "/x"));

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(2, doc.RootElement.GetProperty("itemListElement").GetArrayLength());
        Assert.DoesNotContain("</script>", json);
    }
}
