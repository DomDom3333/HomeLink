using HomeLink.Models;
using HomeLink.Services;

namespace HomeLink.Tests;

public class HumanReadableServiceTests
{
    [Fact]
    public void CreateHumanReadableTextForKnownLocation_ReturnsKnownLocationPhraseWhenStationary()
    {
        LocationInfo location = new()
        {
            MatchedKnownLocation = new KnownLocation("home", "Home", 1, 1),
            Velocity = 0
        };

        string result = HumanReadableService.CreateHumanReadableTextForKnownLocation(location);

        Assert.Contains("Home", result);
        Assert.DoesNotContain("passing by", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CreateHumanReadableTextForKnownLocation_UsesMovingPhraseForFastSpeed()
    {
        LocationInfo location = new()
        {
            MatchedKnownLocation = new KnownLocation("work", "Work", 1, 1),
            Velocity = 55
        };

        string result = HumanReadableService.CreateHumanReadableTextForKnownLocation(location);

        Assert.Contains("Work", result);
        Assert.Matches("^(Passing by|Zooming past|Whizzing by) Work$", result);
    }

    [Fact]
    public void CreateHumanReadableText_AddsMovementPrefixWhenVelocityPresent()
    {
        LocationInfo location = new()
        {
            City = "Vienna",
            District = "2",
            Velocity = 20
        };

        string result = HumanReadableService.CreateHumanReadableText(location);

        Assert.Matches("^(Traveling through|Making my way through|Passing through|On the road|In transit|Cruising through) ", result);
        Assert.True(
            result.Contains("vienna", StringComparison.OrdinalIgnoreCase) ||
            result.Contains("leopoldstadt", StringComparison.OrdinalIgnoreCase) ||
            result.Contains("2nd", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CreateHumanReadableText_WithAddressAndNoLocation_UsesAddressOutput()
    {
        NominatimAddress address = new()
        {
            City = "Prague",
            Country = "Czech Republic",
            District = "Old Town"
        };

        string result = HumanReadableService.CreateHumanReadableText(address);

        Assert.Contains("Prague", result);
    }

    [Fact]
    public void CreateHumanReadableText_WithNullAddress_ReturnsFallbackPhrase()
    {
        string result = HumanReadableService.CreateHumanReadableText((NominatimAddress?)null);

        Assert.Contains(result, new[] { "Somewhere in the world", "Location unknown", "Off the grid" });
    }
}
