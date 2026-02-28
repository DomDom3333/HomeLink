using HomeLink.Models;
using HomeLink.Utils;

namespace HomeLink.Tests;

public class UtilsTests
{
    [Fact]
    public void TruncateText_ReturnsEmpty_ForNullOrEmpty()
    {
        Assert.Equal(string.Empty, TextUtils.TruncateText(string.Empty, 5));
        Assert.Equal(string.Empty, TextUtils.TruncateText(null!, 5));
    }

    [Fact]
    public void TruncateText_ReturnsOriginal_WhenWithinLimit()
    {
        Assert.Equal("hello", TextUtils.TruncateText("hello", 5));
    }

    [Fact]
    public void TruncateText_AddsEllipsis_WhenExceedingLimit()
    {
        Assert.Equal("ab...", TextUtils.TruncateText("abcdefghi", 5));
    }

    [Fact]
    public void BuildCityCountryString_UsesPreferredLocalityAndDistrictRules()
    {
        LocationInfo location = new()
        {
            City = "Vienna",
            Town = "IgnoredTown",
            Village = "IgnoredVillage",
            District = "Leopoldstadt",
            Country = "Austria"
        };

        string result = TextUtils.BuildCityCountryString(location);

        Assert.Equal("Vienna, Leopoldstadt, Austria", result);
    }

    [Fact]
    public void BuildCityCountryString_FallsBackToTownThenVillage()
    {
        Assert.Equal("Krems", TextUtils.BuildCityCountryString(new LocationInfo { Town = "Krems" }));
        Assert.Equal("Hallstatt", TextUtils.BuildCityCountryString(new LocationInfo { Village = "Hallstatt" }));
    }

    [Theory]
    [InlineData("home", "[HOME]")]
    [InlineData("WORK", "[WORK]")]
    [InlineData("airport", "[AIRPORT]")]
    [InlineData("unknown", "[*]")]
    [InlineData(null, "[*]")]
    public void GetLocationIcon_ReturnsExpectedValues(string? iconType, string expected)
    {
        Assert.Equal(expected, TextUtils.GetLocationIcon(iconType));
    }

    [Theory]
    [InlineData(0, "00:00")]
    [InlineData(61000, "01:01")]
    [InlineData(3599000, "59:59")]
    public void FormatTime_FormatsMinutesAndSeconds(long milliseconds, string expected)
    {
        Assert.Equal(expected, TimeUtils.FormatTime(milliseconds));
    }

    [Fact]
    public void LatLonToTile_ReturnsExpectedCoordinatesForKnownPoint()
    {
        var result = GeoUtils.LatLonToTile(48.2082, 16.3738, 16);

        Assert.Equal(35748, result.tileX);
        Assert.Equal(22724, result.tileY);
        Assert.InRange(result.pixelOffsetX, 0, 255);
        Assert.InRange(result.pixelOffsetY, 0, 255);
    }

    [Fact]
    public void CalculateDistance_IsSymmetricAndZeroForSamePoint()
    {
        double zero = GeoUtils.CalculateDistance(48.2, 16.3, 48.2, 16.3);
        double d1 = GeoUtils.CalculateDistance(48.2082, 16.3738, 47.0707, 15.4395);
        double d2 = GeoUtils.CalculateDistance(47.0707, 15.4395, 48.2082, 16.3738);

        Assert.Equal(0d, zero, 10);
        Assert.Equal(d1, d2, 8);
        Assert.InRange(d1, 140_000, 150_000);
    }

    [Theory]
    [InlineData("Katastralgemeinde Karlstetten", "Karlstetten")]
    [InlineData("KATASTRALGEMEINDE Meidling", "Meidling")]           // case-insensitive
    [InlineData("Katastralgemeinde Heinigstetten", "Heinigstetten")]
    [InlineData("Marktgemeinde Korneuburg", "Korneuburg")]
    [InlineData("Marktgemeinde Waidhofen", "Waidhofen")]
    [InlineData("Stadtgemeinde Krems", "Krems")]
    [InlineData("Stadtbezirk Mitte", "Mitte")]
    [InlineData("Gerichtsbezirk Schwechat", "Schwechat")]
    [InlineData("Verwaltungsgemeinschaft Altdorf", "Altdorf")]
    [InlineData("Verbandsgemeinde Landau", "Landau")]
    [InlineData("Samtgemeinde Ilmenau", "Ilmenau")]
    [InlineData("Gemeinde Klosterneuburg", "Klosterneuburg")]
    [InlineData("Ortschaft Grinzing", "Grinzing")]
    [InlineData("Bezirk St. Pölten", "St. Pölten")]
    [InlineData("Rotte Kirchberg", "Kirchberg")]
    [InlineData("Weiler Almbachtal", "Almbachtal")]
    [InlineData("Innere Stadt", "Innere Stadt")] // no prefix → unchanged
    [InlineData("Leopoldstadt", "Leopoldstadt")] // no prefix → unchanged
    [InlineData("Gemeinde", "Gemeinde")] // prefix with no trailing name → unchanged
    [InlineData("Bezirksdorf", "Bezirksdorf")] // "Bezirksdorf" starts with "Bezirk" but has no space after – must not be stripped
    [InlineData("", "")]
    [InlineData(null, null)]
    public void SanitizeLocationName_StripsAdministrativePrefixes(string? input, string? expected)
    {
        Assert.Equal(expected, TextUtils.SanitizeLocationName(input));
    }
}
