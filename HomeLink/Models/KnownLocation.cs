namespace HomeLink.Models;

public class KnownLocation
{
    public string Name { get; set; } = string.Empty;
    public string DisplayText { get; set; } = string.Empty;
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public double RadiusMeters { get; set; } = 100; // Default 100m radius
    public string? Icon { get; set; } // Optional icon identifier (e.g., "home", "work", "gym")
    
    public KnownLocation(string name, string displayText, double latitude, double longitude, double radiusMeters = 100, string? icon = null)
    {
        Name = name;
        DisplayText = displayText;
        Latitude = latitude;
        Longitude = longitude;
        RadiusMeters = radiusMeters;
        Icon = icon;
    }
}