using HomeLink.Models;
using HomeLink.Services;

namespace HomeLink.Tests;

public class DisplayServicesTests
{
    [Fact]
    public void BuildDisplayData_MapsSpotifyAndLocationFields()
    {
        DisplayDataService service = new();
        DisplayMetadata metadata = new() { Width = 640, Height = 384 };
        SpotifyTrackInfo spotify = new()
        {
            Title = new string('T', 40),
            Artist = new string('A', 45),
            Album = new string('B', 50),
            DurationMs = 183000,
            ProgressMs = 61000,
            AlbumCoverUrl = "https://img",
            SpotifyUri = "spotify:track:1",
            ScannableCodeUrl = "https://qr",
            IsPlaying = true
        };

        LocationInfo location = BuildLocation();

        DisplayDataResponse result = service.BuildDisplayData(spotify, location, metadata);

        Assert.True(result.Success);
        Assert.Same(metadata, result.Display);
        Assert.NotNull(result.Spotify);
        Assert.Equal("PLAYING", result.Spotify!.StatusText);
        Assert.Equal("01:01 / 03:03", result.Spotify.ProgressText);
        Assert.EndsWith("...", result.Spotify.TitleDisplay);

        Assert.NotNull(result.Location);
        Assert.Equal("GPS: 48,20820, 16,37380", result.Location!.CoordinatesText);
        Assert.Equal("WiFi", result.Location.DeviceStatus.StatusParts[2]);
        Assert.Equal(16, result.Location.Map.Zoom);
        Assert.Equal(256, result.Location.Map.TileSize);
    }

    [Fact]
    public void BuildDisplayData_HandlesNullInputs()
    {
        DisplayDataService service = new();

        DisplayDataResponse result = service.BuildDisplayData(null, null, new DisplayMetadata());

        Assert.Null(result.Spotify);
        Assert.Null(result.Location);
    }

    [Fact]
    public void BuildDisplayData_UsesFallbackLocationTextAndDistanceInKilometers()
    {
        DisplayDataService service = new();
        LocationInfo location = new()
        {
            Latitude = 48.2082,
            Longitude = 16.3738,
            DisplayName = string.Empty,
            MatchedKnownLocation = new KnownLocation("office", "Office", 47.0707, 15.4395)
        };

        DisplayDataResponse result = service.BuildDisplayData(null, location, new DisplayMetadata());

        Assert.Equal("Unknown Location", result.Location!.LocationText);
        Assert.StartsWith("~", result.Location.KnownLocationDistanceText);
        Assert.Contains("km from center", result.Location.KnownLocationDistanceText);
    }

    [Theory]
    [InlineData("w", "WiFi")]
    [InlineData("m", "Mobile")]
    [InlineData("o", "Offline")]
    [InlineData("sat", "sat")]
    public void BuildDisplayData_MapsConnectionStatus(string connection, string expected)
    {
        DisplayDataService service = new();
        LocationInfo location = BuildLocation();
        location.Connection = connection;

        DisplayDataResponse result = service.BuildDisplayData(null, location, new DisplayMetadata());

        Assert.Contains(expected, result.Location!.DeviceStatus.StatusParts);
    }

    [Fact]
    public void ComputeSourceHash_ChangesForInputMutations_AndBucketsBatteryAndProgress()
    {
        SpotifyTrackInfo spotify = new() { Title = "Song", ProgressMs = 19_999 };
        LocationInfo location = new() { Latitude = 10.123456, Longitude = 20.987654, DisplayName = "x" };

        string baseHash = DisplayFrameHashService.ComputeSourceHash(spotify, location, dither: true, deviceBattery: 57);
        SpotifyTrackInfo sameBucketSpotify = new() { Title = spotify.Title, ProgressMs = 10_001 };
        SpotifyTrackInfo changedSpotify = new() { Title = spotify.Title, ProgressMs = 20_001 };

        string sameBucketHash = DisplayFrameHashService.ComputeSourceHash(
            sameBucketSpotify, location, dither: true, deviceBattery: 59);
        string changedHash = DisplayFrameHashService.ComputeSourceHash(
            changedSpotify, location, dither: false, deviceBattery: 61);

        Assert.Equal(baseHash, sameBucketHash);
        Assert.NotEqual(baseHash, changedHash);
    }

    [Fact]
    public void KnownLocation_Constructor_InitializesAllFields()
    {
        KnownLocation known = new("home", "Home", 1, 2, 150, "home");

        Assert.Equal("home", known.Name);
        Assert.Equal("Home", known.DisplayText);
        Assert.Equal(1, known.Latitude);
        Assert.Equal(2, known.Longitude);
        Assert.Equal(150, known.RadiusMeters);
        Assert.Equal("home", known.Icon);
    }

    private static LocationInfo BuildLocation() => new()
    {
        Latitude = 48.2082,
        Longitude = 16.3738,
        DisplayName = "Vienna",
        HumanReadable = "In Vienna",
        District = "Leopoldstadt",
        City = "Vienna",
        Country = "Austria",
        GoogleMapsUrl = "maps",
        QrCodeUrl = "qr",
        BatteryLevel = 77,
        BatteryStatus = 2,
        Accuracy = 5,
        Altitude = 185,
        Velocity = 12,
        Connection = "w",
        TrackerId = "abc",
        Timestamp = 12345,
        MatchedKnownLocation = new KnownLocation("home", "Home", 48.2082, 16.3738)
    };
}
