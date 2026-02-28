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
        return text[..(maxLength - 3)] + "...";
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

    /// <summary>
    /// Administrative/bureaucratic prefixes that Nominatim sometimes injects into location name
    /// fields (suburb, district, city_district, etc.). Ordered most-specific → least-specific so
    /// compound terms are matched before shorter sub-strings (e.g. "Marktgemeinde" before "Gemeinde").
    /// </summary>
    private static readonly string[] AdministrativePrefixesToStrip =
    [
        "Katastralgemeinde",        // Austrian cadastral municipality (KG)
        "Marktgemeinde",            // Market municipality (AT/DE)
        "Stadtgemeinde",            // City municipality (AT)
        "Stadtbezirk",              // City district (DE)
        "Gerichtsbezirk",           // Judicial district (AT)
        "Verwaltungsgemeinschaft",  // Administrative community (DE)
        "Verbandsgemeinde",         // Association municipality (DE/Rhineland-Palatinate)
        "Samtgemeinde",             // Collective municipality (Lower Saxony)
        "Gemeinde",                 // Generic municipality (AT/DE/CH) — after compound variants
        "Ortschaft",                // Locality/hamlet (AT)
        "Bezirk",                   // District (AT/DE/CH)
        "Rotte",                    // Hamlet (AT)
        "Weiler",                   // Hamlet (AT/DE/CH)
    ];

    /// <summary>
    /// Strips administrative/bureaucratic prefixes from a location name returned by reverse
    /// geocoding so the display stays concise and human-friendly.
    /// <para>
    /// Examples:
    /// <list type="bullet">
    ///   <item>"Katastralgemeinde Karlstetten" → "Karlstetten"</item>
    ///   <item>"Gemeinde Klosterneuburg" → "Klosterneuburg"</item>
    ///   <item>"Bezirk St. Pölten" → "St. Pölten"</item>
    ///   <item>"Innere Stadt" → "Innere Stadt" (no prefix, unchanged)</item>
    /// </list>
    /// </para>
    /// </summary>
    /// <param name="name">Raw location name from Nominatim (may be null).</param>
    /// <returns>Sanitised name, or the original value if no prefix matched.</returns>
    public static string? SanitizeLocationName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return name;

        foreach (string prefix in AdministrativePrefixesToStrip)
        {
            // Require a space after the prefix so we don't partially match real names
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && name.Length > prefix.Length
                && name[prefix.Length] == ' ')
            {
                string stripped = name[(prefix.Length + 1)..].TrimStart();
                if (!string.IsNullOrWhiteSpace(stripped))
                    return stripped;
            }
        }

        return name;
    }
}


