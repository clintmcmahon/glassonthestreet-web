using System.Text.Json;
using GlassOnTheStreet.Web.Services;
using Xunit;

namespace GlassOnTheStreet.Tests;

public class CrimeGroupsTests
{
    [Theory]
    [InlineData("Homicide Offenses", "Homicide Offense", "Crime Offenses (NIBRS)", "homicide")]
    [InlineData("Assault Offenses", "Aggravated Assault", "Crime Offenses (NIBRS)", "agg-assault")]
    [InlineData("Assault Offenses", "Simple Assault", "Crime Offenses (NIBRS)", "simple-assault")]
    [InlineData("Assault Offenses", "Intimidation", "Crime Offenses (NIBRS)", "intimidation")]
    [InlineData("Robbery", "Robbery", "Crime Offenses (NIBRS)", "robbery")]
    [InlineData("Burglary/Breaking & Entering", "Burglary/Breaking & Entering", "Crime Offenses (NIBRS)", "burglary")]
    [InlineData("Larceny/Theft Offenses ", "Theft From Motor Vehicle ", "Crime Offenses (NIBRS)", "theft-from-vehicle")]
    [InlineData("Larceny/Theft Offenses", "Theft of Motor Vehicle Parts or Accessories", "Crime Offenses (NIBRS)", "parts-theft")]
    [InlineData("Larceny/Theft Offenses", "Shoplifting", "Crime Offenses (NIBRS)", "shoplifting")]
    [InlineData("Larceny/Theft Offenses", "All Other Larceny", "Crime Offenses (NIBRS)", "other-larceny")]
    [InlineData("Motor Vehicle Theft", "Motor Vehicle Theft", "Crime Offenses (NIBRS)", "vehicle-theft")]
    [InlineData("Destruction/Damage/Vandalism of Property", "Destruction/Damage/Vandalism of Property", "Crime Offenses (NIBRS)", "vandalism")]
    [InlineData("Kidnapping/Abduction", "Kidnapping/Abduction", "Crime Offenses (NIBRS)", "other")]
    public void Classify_MapsMpdOffensesToPlainLanguageGroups(string category, string offense, string type, string expected)
    {
        Assert.Equal(expected, CrimeGroups.Classify(category, offense, type).Key);
    }

    [Theory]
    [InlineData("Shots Fired Calls", "ShotSpotter Activation (P)", "Shots Fired Calls", "shots-fired")]
    [InlineData("Gunshot Wound Victims", "Gunshot Wound Victims", "Gunshot Wound Victims", "gunshot-victims")]
    [InlineData("Subset of NIBRS Assault Offenses", "Domestic Aggravated Assault - Subset of Assault", "Additional Crime Metrics", "domestic-agg-assault")]
    [InlineData("Subset of NIBRS Robbery", "Carjacking - Subset of Robbery", "Additional Crime Metrics", "carjacking")]
    public void Classify_KeepsNonCrimeMetricsOutOfTheCrimeGroups(string category, string offense, string type, string expected)
    {
        var group = CrimeGroups.Classify(category, offense, type);

        Assert.Equal(expected, group.Key);
        Assert.False(group.IsCrime);
    }

    [Fact]
    public void Groups_HaveUniqueKeys()
    {
        Assert.Equal(CrimeGroups.All.Count, CrimeGroups.All.Select(g => g.Key).Distinct().Count());
    }
}

public class MpdIncidentMapperTests
{
    private static JsonElement Attrs(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void FromAttributes_ConvertsTimeToCentralAndBlockAnchorToDegrees()
    {
        var incident = MpdIncidentMapper.FromAttributes(Attrs("""
            {"Case_Number":"26-281704","Occurred_Date":1790613720000,"Offense":"Aggravated Assault ",
             "Offense_Category":"Assault Offenses","Type":"Crime Offenses (NIBRS)","NIBRS_Code":"13A",
             "NIBRS_Crime_Against":"Person","Crime_Count":2,"Neighborhood":"Bottineau","Ward":1,"Precinct":2,
             "Address":"0021XX CALIFORNIA ST NE","wgsXAnon":-10382646.11878682,"wgsYAnon":5623024.69054276}
            """));

        Assert.NotNull(incident);
        Assert.Equal(new DateOnly(2026, 9, 28), incident!.OccurredDate);
        Assert.Equal(11, incident.OccurredHour); // 16:42 UTC is 11:42 CDT
        Assert.Equal("agg-assault", incident.GroupKey);
        Assert.True(incident.IsCrime);
        Assert.Equal(2, incident.CrimeCount);
        Assert.Equal("26-281704|13A|Aggravated Assault", incident.ExternalKey);
        Assert.Equal((byte)1, incident.Ward);
        Assert.InRange(incident.Lat!.Value, 44.9m, 45.1m);
        Assert.InRange(incident.Lng!.Value, -93.4m, -93.1m);
    }

    [Fact]
    public void FromAttributes_UsesTheCentralDateForLateEveningIncidents()
    {
        // 03:30 UTC on Sep 29 is 22:30 CDT on Sep 28: still the 28th in Minneapolis.
        var incident = MpdIncidentMapper.FromAttributes(Attrs("""
            {"Case_Number":"26-1","Occurred_Date":1790652600000,"Offense":"Simple Assault","Offense_Category":"Assault Offenses"}
            """));

        Assert.Equal(new DateOnly(2026, 9, 28), incident!.OccurredDate);
        Assert.Equal(22, incident.OccurredHour);
    }

    [Fact]
    public void FromAttributes_MarksSubsetRowsAsNotCrimes()
    {
        var incident = MpdIncidentMapper.FromAttributes(Attrs("""
            {"Case_Number":"26-2","Occurred_Date":1790613720000,"Offense":"Domestic Aggravated Assault - Subset of Assault",
             "Offense_Category":"Subset of NIBRS Assault Offenses","Type":"Additional Crime Metrics","NIBRS_Code":"Non NIBRS Data"}
            """));

        Assert.False(incident!.IsCrime);
        Assert.Equal("domestic-agg-assault", incident.GroupKey);
        Assert.Null(incident.Lat);
    }

    [Fact]
    public void FromAttributes_RejectsRowsThatCannotBePlaced()
    {
        Assert.Null(MpdIncidentMapper.FromAttributes(Attrs("""{"Offense":"Simple Assault","Occurred_Date":1790613720000}""")));
        Assert.Null(MpdIncidentMapper.FromAttributes(Attrs("""{"Case_Number":"26-3","Occurred_Date":1790613720000}""")));
        Assert.Null(MpdIncidentMapper.FromAttributes(Attrs("""{"Case_Number":"26-3","Offense":"Simple Assault"}""")));
    }

    [Fact]
    public void FromAttributes_TreatsZeroZeroAndNoAddressAsNoLocation()
    {
        var incident = MpdIncidentMapper.FromAttributes(Attrs("""
            {"Case_Number":"26-4","Occurred_Date":1790613720000,"Offense":"Drug/Narcotic Violations","Offense_Category":"Drug/Narcotic Offenses",
             "NIBRS_Code":"35A","Address":"No Address","wgsXAnon":0,"wgsYAnon":0,"Crime_Count":1}
            """));

        Assert.NotNull(incident);
        Assert.Null(incident!.Lat);
        Assert.Null(incident.Lng);
        Assert.Null(incident.Address);
        // The record itself is kept and counted: only its location is unknown.
        Assert.Equal("drugs", incident.GroupKey);
        Assert.Equal(1, incident.CrimeCount);
    }
}
