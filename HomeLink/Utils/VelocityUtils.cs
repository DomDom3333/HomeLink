namespace HomeLink.Utils;

/// <summary>
/// Helpers for establishing the device speed that drives the location display and phrasing.
/// OwnTracks only fills <c>vel</c> when the underlying fix carries a speed; fused, network and
/// significant-change reports frequently omit it or send a negative value for "unknown", so the
/// speed has to be derived from consecutive fixes instead.
/// </summary>
public static class VelocityUtils
{
    /// <summary>Shorter gaps amplify GPS noise into a fake speed.</summary>
    public const long MinSampleGapSeconds = 5;

    /// <summary>A fix older than this says nothing about the current speed.</summary>
    public const long MaxSampleGapSeconds = 900;

    /// <summary>Below this the movement cannot be told apart from GPS drift.</summary>
    public const double MinSampleDistanceMeters = 30;

    /// <summary>Anything faster is a bad fix, not a real trip.</summary>
    public const double MaxPlausibleSpeedKmh = 1200;

    /// <summary>
    /// Returns the reported speed when it is usable, or null when it is absent, zero, or a negative
    /// "unknown" marker.
    /// </summary>
    public static int? NormalizeReportedVelocity(int? reportedKmh)
    {
        return reportedKmh is > 0 ? reportedKmh : null;
    }

    /// <summary>
    /// Derives a speed in km/h from two consecutive fixes, or null when the pair cannot support a
    /// trustworthy estimate (too close together in time, a stale previous fix, too small a move to
    /// distinguish from GPS drift, or an implausible result).
    /// </summary>
    public static int? DeriveVelocityKmh(
        double previousLatitude,
        double previousLongitude,
        long previousTimestampSeconds,
        double latitude,
        double longitude,
        long timestampSeconds)
    {
        long elapsedSeconds = timestampSeconds - previousTimestampSeconds;
        if (elapsedSeconds is < MinSampleGapSeconds or > MaxSampleGapSeconds)
            return null;

        double distanceMeters = GeoUtils.CalculateDistance(previousLatitude, previousLongitude, latitude, longitude);
        if (distanceMeters < MinSampleDistanceMeters)
            return null;

        double speedKmh = distanceMeters / elapsedSeconds * 3.6;
        if (speedKmh > MaxPlausibleSpeedKmh)
            return null;

        int rounded = (int)Math.Round(speedKmh);
        return rounded > 0 ? rounded : null;
    }
}
