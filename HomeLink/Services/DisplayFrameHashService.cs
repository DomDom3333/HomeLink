using System.Security.Cryptography;
using System.Text;
using HomeLink.Models;

namespace HomeLink.Services;

public static class DisplayFrameHashService
{
    public static string ComputeSourceHash(SpotifyTrackInfo? spotify, LocationInfo? location, bool dither, int? deviceBattery)
    {
        StringBuilder sb = new();

        sb.Append($"dither:{dither}|");

        if (deviceBattery.HasValue)
        {
            int batteryBucket = Math.Clamp(deviceBattery.Value, 0, 100) / 10;
            sb.Append($"deviceBatteryBucket:{batteryBucket}|");
        }

        if (spotify != null)
        {
            sb.Append("spotify:");
            sb.Append($"title:{spotify.Title}|");
            sb.Append($"artist:{spotify.Artist}|");
            sb.Append($"album:{spotify.Album}|");
            sb.Append($"coverUrl:{spotify.AlbumCoverUrl}|");
            sb.Append($"duration:{spotify.DurationMs}|");
            sb.Append($"uri:{spotify.SpotifyUri}|");
            sb.Append($"playing:{spotify.IsPlaying}|");
            long progressBucket10S = Math.Max(0L, spotify.ProgressMs) / 10000L;
            sb.Append($"progressMin:{progressBucket10S}|");
        }

        if (location != null)
        {
            sb.Append("location:");
            sb.Append($"lat:{Math.Round(location.Latitude, 5)}|");
            sb.Append($"lon:{Math.Round(location.Longitude, 5)}|");
            sb.Append($"name:{location.DisplayName}|");
            sb.Append($"district:{location.District}|");
            sb.Append($"city:{location.City}|");
            sb.Append($"town:{location.Town}|");
            sb.Append($"village:{location.Village}|");
            sb.Append($"country:{location.Country}|");
            sb.Append($"knownLoc:{location.MatchedKnownLocation?.Name}|");
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash);
    }
}
