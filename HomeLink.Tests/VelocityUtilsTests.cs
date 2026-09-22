using HomeLink.Utils;

namespace HomeLink.Tests;

public class VelocityUtilsTests
{
    // Vienna Hauptbahnhof, used as the starting point for the derived-speed cases.
    private const double BaseLatitude = 48.1854;
    private const double BaseLongitude = 16.3760;
    private const long BaseTimestamp = 1738070400;

    /// <summary>
    /// Offsets a latitude by an approximate number of metres due north (~111 320 m per degree).
    /// </summary>
    private static double LatitudeOffsetMeters(double meters) => BaseLatitude + meters / 111320.0;

    [Theory]
    [InlineData(null)]
    [InlineData(-1)]
    [InlineData(0)]
    public void NormalizeReportedVelocity_TreatsMissingZeroAndNegativeAsUnknown(int? reported)
    {
        Assert.Null(VelocityUtils.NormalizeReportedVelocity(reported));
    }

    [Fact]
    public void NormalizeReportedVelocity_KeepsPositiveReading()
    {
        Assert.Equal(87, VelocityUtils.NormalizeReportedVelocity(87));
    }

    [Fact]
    public void DeriveVelocityKmh_ReturnsTrainSpeedForLongMoveBetweenFixes()
    {
        // 2 km in 60 s == 120 km/h.
        int? derived = VelocityUtils.DeriveVelocityKmh(
            BaseLatitude, BaseLongitude, BaseTimestamp,
            LatitudeOffsetMeters(2000), BaseLongitude, BaseTimestamp + 60);

        Assert.NotNull(derived);
        Assert.InRange(derived!.Value, 118, 122);
    }

    [Fact]
    public void DeriveVelocityKmh_ReturnsNullForDriftSizedMove()
    {
        int? derived = VelocityUtils.DeriveVelocityKmh(
            BaseLatitude, BaseLongitude, BaseTimestamp,
            LatitudeOffsetMeters(10), BaseLongitude, BaseTimestamp + 60);

        Assert.Null(derived);
    }

    [Fact]
    public void DeriveVelocityKmh_ReturnsNullWhenFixesAreTooCloseInTime()
    {
        int? derived = VelocityUtils.DeriveVelocityKmh(
            BaseLatitude, BaseLongitude, BaseTimestamp,
            LatitudeOffsetMeters(2000), BaseLongitude, BaseTimestamp + 1);

        Assert.Null(derived);
    }

    [Fact]
    public void DeriveVelocityKmh_ReturnsNullWhenPreviousFixIsStale()
    {
        int? derived = VelocityUtils.DeriveVelocityKmh(
            BaseLatitude, BaseLongitude, BaseTimestamp,
            LatitudeOffsetMeters(2000), BaseLongitude, BaseTimestamp + VelocityUtils.MaxSampleGapSeconds + 1);

        Assert.Null(derived);
    }

    [Fact]
    public void DeriveVelocityKmh_ReturnsNullForImplausibleSpeed()
    {
        // 500 km in 60 s == 30 000 km/h, which can only be a bad fix.
        int? derived = VelocityUtils.DeriveVelocityKmh(
            BaseLatitude, BaseLongitude, BaseTimestamp,
            LatitudeOffsetMeters(500000), BaseLongitude, BaseTimestamp + 60);

        Assert.Null(derived);
    }

    [Fact]
    public void DeriveVelocityKmh_ReturnsNullForOutOfOrderFixes()
    {
        int? derived = VelocityUtils.DeriveVelocityKmh(
            BaseLatitude, BaseLongitude, BaseTimestamp,
            LatitudeOffsetMeters(2000), BaseLongitude, BaseTimestamp - 60);

        Assert.Null(derived);
    }
}
