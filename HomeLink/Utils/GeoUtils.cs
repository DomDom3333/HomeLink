namespace HomeLink.Utils;

/// <summary>
/// Geographic utility methods for coordinate calculations
/// </summary>
public static class GeoUtils
{
    private const double EarthRadiusMeters = 6371000;

    /// <summary>
    /// Converts latitude/longitude to tile coordinates for map rendering
    /// </summary>
    public static (int tileX, int tileY, int pixelOffsetX, int pixelOffsetY) LatLonToTile(double lat, double lon, int zoom)
    {
        double n = Math.Pow(2, zoom);
        double latRad = lat * Math.PI / 180;
        
        double tileXExact = (lon + 180.0) / 360.0 * n;
        double tileYExact = (1.0 - Math.Log(Math.Tan(latRad) + 1.0 / Math.Cos(latRad)) / Math.PI) / 2.0 * n;
        
        int tileX = (int)Math.Floor(tileXExact);
        int tileY = (int)Math.Floor(tileYExact);
        
        // Calculate pixel offset within the tile (tiles are 256x256)
        int pixelOffsetX = (int)((tileXExact - tileX) * 256);
        int pixelOffsetY = (int)((tileYExact - tileY) * 256);
        
        return (tileX, tileY, pixelOffsetX, pixelOffsetY);
    }

    /// <summary>
    /// Calculates distance between two coordinates using Haversine formula
    /// </summary>
    public static double CalculateDistance(double lat1, double lon1, double lat2, double lon2)
    {
        double dLat = DegreesToRadians(lat2 - lat1);
        double dLon = DegreesToRadians(lon2 - lon1);

        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                   Math.Cos(DegreesToRadians(lat1)) * Math.Cos(DegreesToRadians(lat2)) *
                   Math.Sin(dLon / 2) * Math.Sin(dLon / 2);

        double c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));

        return EarthRadiusMeters * c;
    }

    /// <summary>
    /// Converts degrees to radians
    /// </summary>
    private static double DegreesToRadians(double degrees)
    {
        return degrees * Math.PI / 180;
    }
}

