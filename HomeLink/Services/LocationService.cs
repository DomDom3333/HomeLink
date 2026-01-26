using System.Text.Json;
using System.Text.Json.Serialization;
using System.Globalization;

namespace HomeLink.Services;

public class LocationInfo
{
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string HumanReadable { get; set; } = string.Empty;
    public string? District { get; set; }
    public string? City { get; set; }
    public string? Town { get; set; }
    public string? Village { get; set; }
    public string? Country { get; set; }
    public KnownLocation? MatchedKnownLocation { get; set; }
    public string GoogleMapsUrl { get; set; } = string.Empty;
    public string QrCodeUrl { get; set; } = string.Empty;
    
    // OwnTracks metadata
    /// <summary>Battery level percentage (0-100)</summary>
    public int? BatteryLevel { get; set; }
    
    /// <summary>Battery status: 0=unknown, 1=unplugged, 2=charging, 3=full</summary>
    public int? BatteryStatus { get; set; }
    
    /// <summary>GPS accuracy in meters</summary>
    public int? Accuracy { get; set; }
    
    /// <summary>Altitude in meters above sea level</summary>
    public int? Altitude { get; set; }
    
    /// <summary>Velocity/speed in km/h</summary>
    public int? Velocity { get; set; }
    
    /// <summary>Connection type: w=WiFi, o=offline, m=mobile</summary>
    public string? Connection { get; set; }
    
    /// <summary>Tracker ID from OwnTracks</summary>
    public string? TrackerId { get; set; }
    
    /// <summary>Timestamp of when location was recorded (Unix epoch)</summary>
    public long? Timestamp { get; set; }
}

/// <summary>
/// Metadata from OwnTracks payload to enrich location information
/// </summary>
public class OwnTracksMetadata
{
    public int? BatteryLevel { get; set; }
    public int? BatteryStatus { get; set; }
    public int? Accuracy { get; set; }
    public int? Altitude { get; set; }
    public int? Velocity { get; set; }
    public string? Connection { get; set; }
    public string? TrackerId { get; set; }
    public long? Timestamp { get; set; }
}

public class KnownLocation
{
    public string Name { get; set; } = string.Empty;
    public string DisplayText { get; set; } = string.Empty;
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public double RadiusMeters { get; set; } = 100; // Default 100m radius
    public string? Icon { get; set; } // Optional icon identifier (e.g., "home", "work", "gym")

    public KnownLocation() { }

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

public class LocationService
{
    private readonly HttpClient _httpClient;
    private readonly List<KnownLocation> _knownLocations = new();
    private const string NominatimBaseUrl = "https://nominatim.openstreetmap.org/reverse";
    private const double EarthRadiusMeters = 6371000;
    
    // Cached location data
    private LocationInfo? _cachedLocation;
    private DateTime _cachedLocationTimestamp = DateTime.MinValue;

    public LocationService(HttpClient httpClient)
    {
        _httpClient = httpClient;
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("HomeLink/1.0");

        // Load known locations from environment variable KNOWN_LOCATIONS (see loader comment for format)
        LoadKnownLocationsFromEnv();
    }

    #region Cached Location

    /// <summary>
    /// Gets the cached location if available.
    /// </summary>
    public LocationInfo? GetCachedLocation() => _cachedLocation;

    /// <summary>
    /// Gets the timestamp of when the cached location was last updated.
    /// </summary>
    public DateTime GetCachedLocationTimestamp() => _cachedLocationTimestamp;

    /// <summary>
    /// Updates the cached location with the provided coordinates.
    /// </summary>
    public async Task<LocationInfo?> UpdateCachedLocationAsync(double latitude, double longitude, OwnTracksMetadata? metadata = null)
    {
        var location = await GetLocationFromCoordinatesAsync(latitude, longitude);
        if (location != null)
        {
            // Apply OwnTracks metadata if provided
            if (metadata != null)
            {
                location.BatteryLevel = metadata.BatteryLevel;
                location.BatteryStatus = metadata.BatteryStatus;
                location.Accuracy = metadata.Accuracy;
                location.Altitude = metadata.Altitude;
                location.Velocity = metadata.Velocity;
                location.Connection = metadata.Connection;
                location.TrackerId = metadata.TrackerId;
                location.Timestamp = metadata.Timestamp;
                
                // Regenerate human readable text now that we have velocity/movement info
                if (location.MatchedKnownLocation == null)
                {
                    location.HumanReadable = CreateHumanReadableText(location);
                }
                else
                {
                    // For known locations, add movement context if moving
                    location.HumanReadable = CreateHumanReadableTextForKnownLocation(location);
                }
            }
            
            _cachedLocation = location;
            _cachedLocationTimestamp = DateTime.UtcNow;
        }
        return location;
    }

    /// <summary>
    /// Gets the cached location if available, otherwise fetches from coordinates.
    /// </summary>
    public async Task<LocationInfo?> GetLocationAsync(double? latitude = null, double? longitude = null)
    {
        // If coordinates provided, fetch fresh data
        if (latitude.HasValue && longitude.HasValue)
        {
            return await GetLocationFromCoordinatesAsync(latitude.Value, longitude.Value);
        }
        
        // Return cached location
        return _cachedLocation;
    }

    #endregion

    #region Known Locations Management

    /// <summary>
    /// Adds a known location with the specified parameters.
    /// </summary>
    public void AddKnownLocation(string name, string displayText, double latitude, double longitude, double radiusMeters = 100, string? icon = null)
    {
        _knownLocations.Add(new KnownLocation(name, displayText, latitude, longitude, radiusMeters, icon));
    }

    /// <summary>
    /// Gets all configured known locations.
    /// </summary>
    public IReadOnlyList<KnownLocation> GetKnownLocations() => _knownLocations.AsReadOnly();

    /// <summary>
    /// Finds a known location that matches the given coordinates.
    /// Returns the closest match if multiple locations overlap.
    /// </summary>
    public KnownLocation? FindKnownLocation(double latitude, double longitude)
    {
        KnownLocation? closestMatch = null;
        double closestDistance = double.MaxValue;

        foreach (var location in _knownLocations)
        {
            var distance = CalculateDistanceMeters(latitude, longitude, location.Latitude, location.Longitude);
            if (distance <= location.RadiusMeters && distance < closestDistance)
            {
                closestMatch = location;
                closestDistance = distance;
            }
        }

        return closestMatch;
    }

    /// <summary>
    /// Calculates the distance between two coordinates using the Haversine formula.
    /// </summary>
    public static double CalculateDistanceMeters(double lat1, double lon1, double lat2, double lon2)
    {
        var dLat = DegreesToRadians(lat2 - lat1);
        var dLon = DegreesToRadians(lon2 - lon1);

        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(DegreesToRadians(lat1)) * Math.Cos(DegreesToRadians(lat2)) *
                Math.Sin(dLon / 2) * Math.Sin(dLon / 2);

        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));

        return EarthRadiusMeters * c;
    }

    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180;

    #endregion

    #region Maps and QR Code Generation

    /// <summary>
    /// Generates a Google Maps URL for the given coordinates.
    /// </summary>
    public static string GenerateGoogleMapsUrl(double latitude, double longitude, string? label = null)
    {
        var baseUrl = "https://maps.google.com/maps";
        // Use invariant culture for coordinates to avoid locale decimal separator issues
        var coordinates = $"{latitude.ToString("F6", CultureInfo.InvariantCulture)},{longitude.ToString("F6", CultureInfo.InvariantCulture)}";
        
        if (!string.IsNullOrEmpty(label))
        {
            // Use the label as a query with coordinates
            var encodedLabel = Uri.EscapeDataString(label);
            return $"{baseUrl}?q={encodedLabel}@{coordinates}";
        }
        
        // Simple coordinate search
        return $"{baseUrl}?q={coordinates}";
    }

    /// <summary>
    /// Generates a QR code URL using the Google Charts API for the Google Maps URL.
    /// </summary>
    public static string GenerateQrCodeUrl(string googleMapsUrl, int size = 200)
    {
        var encodedUrl = Uri.EscapeDataString(googleMapsUrl);
        return $"https://chart.googleapis.com/chart?chs={size}x{size}&cht=qr&chl={encodedUrl}";
    }

    /// <summary>
    /// Generates a QR code URL directly from coordinates.
    /// </summary>
    public static string GenerateQrCodeForCoordinates(double latitude, double longitude, string? label = null, int qrSize = 200)
    {
        var mapsUrl = GenerateGoogleMapsUrl(latitude, longitude, label);
        return GenerateQrCodeUrl(mapsUrl, qrSize);
    }

    #endregion

    public async Task<LocationInfo?> GetLocationFromCoordinatesAsync(double latitude, double longitude)
    {
        // First check if this matches a known location
        var knownLocation = FindKnownLocation(latitude, longitude);
        
        try
        {
            // Format coordinates using invariant culture to ensure decimal point is used (not comma in some locales)
            var latStr = latitude.ToString("G", CultureInfo.InvariantCulture);
            var lonStr = longitude.ToString("G", CultureInfo.InvariantCulture);
            var url = $"{NominatimBaseUrl}?lat={latStr}&lon={lonStr}&format=json&addressdetails=1";

            var response = await _httpClient.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                // Read body for diagnostics (Nominatim often returns useful error details)
                var errorBody = await response.Content.ReadAsStringAsync();
                throw new HttpRequestException($"Nominatim API returned {(int)response.StatusCode} {response.ReasonPhrase}: {errorBody}");
            }
             
             var json = await response.Content.ReadAsStringAsync();
             var nominatimResponse = JsonSerializer.Deserialize<NominatimResponse>(json);
            
            if (nominatimResponse == null)
                return null;

            var address = nominatimResponse.Address;
            
            // Generate Google Maps URL and QR code
            var mapsLabel = knownLocation?.DisplayText ?? CreateHumanReadableText(address);
            var googleMapsUrl = GenerateGoogleMapsUrl(latitude, longitude, mapsLabel);
            var qrCodeUrl = GenerateQrCodeUrl(googleMapsUrl);
            
            var locationInfo = new LocationInfo
            {
                Latitude = latitude,
                Longitude = longitude,
                DisplayName = nominatimResponse.DisplayName ?? string.Empty,
                District = address?.Suburb ?? address?.CityDistrict ?? address?.District,
                City = address?.City,
                Town = address?.Town,
                Village = address?.Village,
                Country = address?.Country,
                MatchedKnownLocation = knownLocation,
                GoogleMapsUrl = googleMapsUrl,
                QrCodeUrl = qrCodeUrl
            };
            
            // Generate human readable text (will be updated with velocity context when metadata is applied)
            locationInfo.HumanReadable = knownLocation != null 
                ? knownLocation.DisplayText 
                : CreateHumanReadableText(address, locationInfo);

            return locationInfo;
        }
        catch (Exception)
        {
            // If API fails but we have a known location, return that
            if (knownLocation != null)
            {
                var googleMapsUrl = GenerateGoogleMapsUrl(latitude, longitude, knownLocation.DisplayText);
                var qrCodeUrl = GenerateQrCodeUrl(googleMapsUrl);
                
                return new LocationInfo
                {
                    Latitude = latitude,
                    Longitude = longitude,
                    MatchedKnownLocation = knownLocation,
                    HumanReadable = knownLocation.DisplayText,
                    GoogleMapsUrl = googleMapsUrl,
                    QrCodeUrl = qrCodeUrl
                };
            }
            return null;
        }
    }

    #region Human Readable Text Generation

    /// <summary>
    /// Creates a human-readable location text from LocationInfo with full context including movement.
    /// </summary>
    private static string CreateHumanReadableText(LocationInfo location)
    {
        var baseText = CreateBaseLocationText(location);
        var movementContext = GetMovementContext(location.Velocity);
        
        if (!string.IsNullOrEmpty(movementContext))
        {
            return $"{movementContext} {baseText.ToLowerInvariant()}";
        }
        
        return baseText;
    }

    /// <summary>
    /// Creates human-readable text for a known location, adding movement context if applicable.
    /// </summary>
    private static string CreateHumanReadableTextForKnownLocation(LocationInfo location)
    {
        var knownLocation = location.MatchedKnownLocation;
        if (knownLocation == null)
            return CreateHumanReadableText(location);
        
        // If moving at significant speed, they might be leaving/arriving
        if (location.Velocity.HasValue && location.Velocity.Value > 5)
        {
            // Moving while at a known location - likely arriving or leaving
            if (location.Velocity.Value > 30)
            {
                return $"Passing by {knownLocation.DisplayText}";
            }
            return $"Near {knownLocation.DisplayText}";
        }
        
        // Stationary at known location
        return $"At {knownLocation.DisplayText}";
    }

    /// <summary>
    /// Creates a human-readable location text from address only (backward compatible).
    /// </summary>
    private static string CreateHumanReadableText(NominatimAddress? address, LocationInfo? location = null)
    {
        var baseText = CreateBaseLocationText(address);
        
        // If we have location metadata, add movement context
        if (location != null)
        {
            var movementContext = GetMovementContext(location.Velocity);
            if (!string.IsNullOrEmpty(movementContext))
            {
                return $"{movementContext} {baseText.ToLowerInvariant()}";
            }
        }
        
        return baseText;
    }

    /// <summary>
    /// Gets a descriptive movement prefix based on velocity.
    /// </summary>
    private static string GetMovementContext(int? velocityKmh)
    {
        if (!velocityKmh.HasValue || velocityKmh.Value < 2)
            return string.Empty; // Stationary or GPS drift
        
        return velocityKmh.Value switch
        {
            < 6 => "Walking",           // 2-5 km/h - walking pace
            < 15 => "Strolling",        // 6-14 km/h - brisk walk or slow bike
            < 25 => "Cycling",          // 15-24 km/h - typical cycling speed
            < 50 => "Traveling",        // 25-49 km/h - city driving or fast cycling
            < 90 => "Driving",          // 50-89 km/h - highway driving
            < 150 => "Speeding along",  // 90-149 km/h - fast highway
            < 300 => "On a train",      // 150-299 km/h - high-speed train
            _ => "Flying"               // 300+ km/h - airplane
        };
    }

    /// <summary>
    /// Creates the base location text from LocationInfo.
    /// </summary>
    private static string CreateBaseLocationText(LocationInfo location)
    {
        // Try to build location from available properties
        var locality = location.City ?? location.Town ?? location.Village;
        var district = location.District;
        var country = location.Country;

        // Check if it's Vienna based on stored properties
        if (IsViennaCity(locality))
        {
            return CreateViennaLocationText(district, locality);
        }

        return CreateGenericLocationText(district, locality, null, country);
    }

    /// <summary>
    /// Creates the base location text from NominatimAddress.
    /// </summary>
    private static string CreateBaseLocationText(NominatimAddress? address)
    {
        if (address == null)
            return "Somewhere in the world";

        // Check if it's Vienna (Wien) - handle district format
        if (IsVienna(address))
        {
            var districtName = address.Suburb ?? address.CityDistrict ?? address.District;
            var locality = address.City ?? address.Town;
            return CreateViennaLocationText(districtName, locality);
        }

        // Get locality and district
        var genericLocality = address.City ?? address.Town ?? address.Village ?? address.Municipality;
        var genericDistrict = address.Suburb ?? address.CityDistrict ?? address.District;
        
        return CreateGenericLocationText(genericDistrict, genericLocality, address.State, address.Country);
    }

    /// <summary>
    /// Creates location text specifically for Vienna with district handling.
    /// </summary>
    private static string CreateViennaLocationText(string? districtName, string? _)
    {
        if (!string.IsNullOrEmpty(districtName))
        {
            var districtNumber = ExtractViennaDistrictNumber(districtName);
            if (districtNumber.HasValue)
            {
                var districtOrdinal = GetOrdinal(districtNumber.Value);
                var friendlyName = GetViennaDistrictFriendlyName(districtNumber.Value);
                
                if (!string.IsNullOrEmpty(friendlyName))
                {
                    return $"In {friendlyName}, the {districtOrdinal} district of Vienna";
                }
                return $"In the {districtOrdinal} district of Vienna";
            }
            return $"In {districtName}, Vienna";
        }
        return "Somewhere in Vienna";
    }

    /// <summary>
    /// Creates generic location text for non-Vienna locations.
    /// </summary>
    private static string CreateGenericLocationText(string? district, string? locality, string? state, string? country)
    {
        var parts = new List<string>();

        // Build location hierarchy
        if (!string.IsNullOrEmpty(district) && district != locality)
        {
            parts.Add(district);
        }

        if (!string.IsNullOrEmpty(locality))
        {
            parts.Add(locality);
        }

        // Add country context for international flavor
        if (!string.IsNullOrEmpty(country) && parts.Count > 0)
        {
            // Only add country if it's different from expected context
            var countryContext = GetCountryContext(country);
            if (!string.IsNullOrEmpty(countryContext))
            {
                return $"In {string.Join(", ", parts)}, {countryContext}";
            }
            return $"In {string.Join(", ", parts)}";
        }

        if (parts.Count > 0)
        {
            return $"In {string.Join(", ", parts)}";
        }

        // Fallback to state/country
        if (!string.IsNullOrEmpty(state))
        {
            if (!string.IsNullOrEmpty(country))
            {
                return $"Somewhere in {state}, {country}";
            }
            return $"Somewhere in {state}";
        }

        if (!string.IsNullOrEmpty(country))
        {
            return $"Somewhere in {country}";
        }

        return "Somewhere in the world";
    }

    /// <summary>
    /// Returns a friendly context string for a country, or empty if it should be omitted.
    /// </summary>
    private static string GetCountryContext(string country)
    {
        // Return abbreviated or common names for well-known countries
        var normalizedCountry = country.ToLowerInvariant();
        
        return normalizedCountry switch
        {
            "austria" or "österreich" => "Austria",
            "germany" or "deutschland" => "Germany",
            "switzerland" or "schweiz" or "suisse" => "Switzerland",
            "united states" or "united states of america" or "usa" => "USA",
            "united kingdom" or "uk" => "UK",
            "czech republic" or "czechia" or "česko" => "Czechia",
            "hungary" or "magyarország" => "Hungary",
            "slovakia" or "slovensko" => "Slovakia",
            "italy" or "italia" => "Italy",
            "france" => "France",
            "spain" or "españa" => "Spain",
            "netherlands" or "nederland" => "Netherlands",
            _ => country // Return as-is for other countries
        };
    }

    /// <summary>
    /// Gets a friendly name for Vienna districts (the neighborhood name).
    /// </summary>
    private static string? GetViennaDistrictFriendlyName(int districtNumber)
    {
        return districtNumber switch
        {
            1 => "Innere Stadt",
            2 => "Leopoldstadt",
            3 => "Landstraße",
            4 => "Wieden",
            5 => "Margareten",
            6 => "Mariahilf",
            7 => "Neubau",
            8 => "Josefstadt",
            9 => "Alsergrund",
            10 => "Favoriten",
            11 => "Simmering",
            12 => "Meidling",
            13 => "Hietzing",
            14 => "Penzing",
            15 => "Rudolfsheim-Fünfhaus",
            16 => "Ottakring",
            17 => "Hernals",
            18 => "Währing",
            19 => "Döbling",
            20 => "Brigittenau",
            21 => "Floridsdorf",
            22 => "Donaustadt",
            23 => "Liesing",
            _ => null
        };
    }

    /// <summary>
    /// Checks if a city name is Vienna.
    /// </summary>
    private static bool IsViennaCity(string? cityName)
    {
        if (string.IsNullOrEmpty(cityName))
            return false;
            
        return cityName.Equals("Wien", StringComparison.OrdinalIgnoreCase) ||
               cityName.Equals("Vienna", StringComparison.OrdinalIgnoreCase);
    }

    #endregion

    #region Vienna District Helpers
    
    private static bool IsVienna(NominatimAddress address)
    {
        var city = address.City ?? address.Town ?? "";
        return city.Equals("Wien", StringComparison.OrdinalIgnoreCase) ||
               city.Equals("Vienna", StringComparison.OrdinalIgnoreCase) ||
               (address.State?.Contains("Wien", StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private static int? ExtractViennaDistrictNumber(string districtName)
    {
        // Vienna districts are often named like "Innere Stadt" (1st), "Leopoldstadt" (2nd), etc.
        // Or they might contain the district number directly
        
        // Try to extract a number from the district name
        var match = System.Text.RegularExpressions.Regex.Match(districtName, @"(\d+)");
        if (match.Success && int.TryParse(match.Groups[1].Value, out var number))
        {
            return number;
        }

        // Map common Vienna district names to numbers
        var districtMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            { "Innere Stadt", 1 },
            { "Leopoldstadt", 2 },
            { "Landstraße", 3 },
            { "Wieden", 4 },
            { "Margareten", 5 },
            { "Mariahilf", 6 },
            { "Neubau", 7 },
            { "Josefstadt", 8 },
            { "Alsergrund", 9 },
            { "Favoriten", 10 },
            { "Simmering", 11 },
            { "Meidling", 12 },
            { "Hietzing", 13 },
            { "Penzing", 14 },
            { "Rudolfsheim-Fünfhaus", 15 },
            { "Ottakring", 16 },
            { "Hernals", 17 },
            { "Währing", 18 },
            { "Döbling", 19 },
            { "Brigittenau", 20 },
            { "Floridsdorf", 21 },
            { "Donaustadt", 22 },
            { "Liesing", 23 }
        };

        foreach (var kvp in districtMap)
        {
            if (districtName.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase))
            {
                return kvp.Value;
            }
        }

        return null;
    }

    #endregion

    private static string GetOrdinal(int number)
    {
        if (number <= 0) return number.ToString();

        var suffix = (number % 100) switch
        {
            11 or 12 or 13 => "th",
            _ => (number % 10) switch
            {
                1 => "st",
                2 => "nd",
                3 => "rd",
                _ => "th"
            }
        };

        return $"{number}{suffix}";
    }

    /// <summary>
    /// Loads known locations from the environment variable <c>KNOWN_LOCATIONS</c>.
    /// Simple format (designed for Docker/Unraid env):
    ///   name|DisplayText|lat|lon|radius;name2|Display2|lat|lon|radius
    /// - entries are separated by semicolons ';'
    /// - each entry fields are separated by pipe '|' characters
    /// - fields: name (machine id), DisplayText (user label), latitude, longitude, optional radius in meters (defaults to 100)
    /// Example:
    ///   KNOWN_LOCATIONS=home|Home|48.258085|15.55572526|100;work|Work|48.20305|16.39182|150;kathis|Kathi's|48.1499|16.29423|100
    ///
    /// This keeps personal coordinates out of the repository and lets you provide them at container-runtime.
    /// </summary>
    private void LoadKnownLocationsFromEnv()
    {
        var env = Environment.GetEnvironmentVariable("KNOWN_LOCATIONS");
        if (string.IsNullOrWhiteSpace(env))
            return;

        var entries = env.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var rawEntry in entries)
        {
            var entry = rawEntry.Trim();
            if (string.IsNullOrEmpty(entry))
                continue;

            var parts = entry.Split('|', StringSplitOptions.TrimEntries);
            if (parts.Length < 4)
                continue; // need at least name|display|lat|lon

            if (!double.TryParse(parts[2], NumberStyles.Any, CultureInfo.InvariantCulture, out var lat))
                continue;
            if (!double.TryParse(parts[3], NumberStyles.Any, CultureInfo.InvariantCulture, out var lon))
                continue;

            var name = parts[0];
            var display = parts[1];
            var radius = 100.0;
            if (parts.Length >= 5 && double.TryParse(parts[4], NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedRadius))
                radius = parsedRadius;

            AddKnownLocation(name, display, lat, lon, radius);
        }
    }
}

// Models for Nominatim API response
internal class NominatimResponse
{
    [JsonPropertyName("display_name")]
    public string? DisplayName { get; set; }

    [JsonPropertyName("address")]
    public NominatimAddress? Address { get; set; }
}

internal class NominatimAddress
{
    [JsonPropertyName("suburb")]
    public string? Suburb { get; set; }

    [JsonPropertyName("city_district")]
    public string? CityDistrict { get; set; }

    [JsonPropertyName("district")]
    public string? District { get; set; }

    [JsonPropertyName("city")]
    public string? City { get; set; }

    [JsonPropertyName("town")]
    public string? Town { get; set; }

    [JsonPropertyName("village")]
    public string? Village { get; set; }

    [JsonPropertyName("municipality")]
    public string? Municipality { get; set; }

    [JsonPropertyName("state")]
    public string? State { get; set; }

    [JsonPropertyName("country")]
    public string? Country { get; set; }
}