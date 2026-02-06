using HomeLink.Models;

namespace HomeLink.Utils;

/// <summary>
/// Text manipulation and formatting utility methods
/// </summary>
public static class TextUtils
{
    /// <summary>
    /// Truncates text to fit within character limit
    /// </summary>
    public static string TruncateText(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        if (text.Length <= maxLength) return text;
        return text.Substring(0, maxLength - 3) + "...";
    }

    /// <summary>
    /// Builds a city/country string from location data
    /// </summary>
    public static string BuildCityCountryString(LocationInfo locationData)
    {
        List<string> parts = new List<string>();

        // Add city/town/village (prefer city, then town, then village)
        if (!string.IsNullOrEmpty(locationData.City))
            parts.Add(locationData.City);
        else if (!string.IsNullOrEmpty(locationData.Town))
            parts.Add(locationData.Town);
        else if (!string.IsNullOrEmpty(locationData.Village))
            parts.Add(locationData.Village);

        // Add district if different from city
        if (!string.IsNullOrEmpty(locationData.District) && 
            locationData.District != locationData.City &&
            locationData.District != locationData.Town)
        {
            parts.Add(locationData.District);
        }

        // Add country
        if (!string.IsNullOrEmpty(locationData.Country))
            parts.Add(locationData.Country);

        return string.Join(", ", parts);
    }

    /// <summary>
    /// Gets an icon prefix for known location types
    /// </summary>
    public static string GetLocationIcon(string? iconType)
    {
        return iconType?.ToLower() switch
        {
            "home" => "[HOME]",
            "work" => "[WORK]",
            "gym" => "[GYM]",
            "school" => "[SCHOOL]",
            "shop" => "[SHOP]",
            "restaurant" => "[FOOD]",
            "cafe" => "[CAFE]",
            "park" => "[PARK]",
            "hospital" => "[HOSPITAL]",
            "airport" => "[AIRPORT]",
            "station" => "[STATION]",
            "hotel" => "[HOTEL]",
            "friend" => "[FRIEND]",
            "family" => "[FAMILY]",
            _ => "[*]"
        };
    }
}


