using System.Text.Json;
using System.Globalization;
using HomeLink.Models;

namespace HomeLink.Services;

public class LocationService
{
    private readonly HttpClient _httpClient;
    private readonly HumanReadableService _humanReadableService;
    private readonly StatePersistenceService _statePersistenceService;
    private readonly List<KnownLocation> _knownLocations = new();
    private const string NominatimBaseUrl = "https://nominatim.openstreetmap.org/reverse";
    private const double EarthRadiusMeters = 6371000;
    
    // Cached location data
    private LocationInfo? _cachedLocation;

    public LocationService(HttpClient httpClient, StatePersistenceService statePersistenceService)
    {
        _httpClient = httpClient;
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("HomeLink/1.0");
        _humanReadableService = new HumanReadableService();
        _statePersistenceService = statePersistenceService;

        // Load known locations from environment variable KNOWN_LOCATIONS (see loader comment for format)
        LoadKnownLocationsFromEnv();

        _cachedLocation = _statePersistenceService.LoadLocationAsync().GetAwaiter().GetResult();
    }

    #region Cached Location

    /// <summary>
    /// Gets the cached location if available.
    /// </summary>
    public LocationInfo? GetCachedLocation() => _cachedLocation;

    /// <summary>
    /// Persists a raw location snapshot without any network/geocoding work.
    /// </summary>
    public async Task<LocationInfo> SaveRawLocationSnapshot(double latitude, double longitude, OwnTracksMetadata? metadata = null)
    {
        string googleMapsUrl = GenerateGoogleMapsUrl(latitude, longitude);
        string qrCodeUrl = GenerateQrCodeUrl(googleMapsUrl);

        LocationInfo location = new()
        {
            Latitude = latitude,
            Longitude = longitude,
            HumanReadable = "Locating…",
            GoogleMapsUrl = googleMapsUrl,
            QrCodeUrl = qrCodeUrl
        };

        ApplyOwnTracksMetadata(location, metadata);

        _cachedLocation = location;
        await _statePersistenceService.SaveLocationAsync(location);
        return location;
    }

    /// <summary>
    /// Performs reverse geocoding and known-location matching for the provided snapshot.
    /// </summary>
    public async Task<LocationInfo?> EnrichLocationAsync(LocationInfo rawSnapshot)
    {
        LocationInfo? enriched = await GetLocationFromCoordinatesAsync(rawSnapshot.Latitude, rawSnapshot.Longitude);
        if (enriched == null)
            return null;

        ApplyOwnTracksMetadata(enriched, new OwnTracksMetadata
        {
            BatteryLevel = rawSnapshot.BatteryLevel,
            BatteryStatus = rawSnapshot.BatteryStatus,
            Accuracy = rawSnapshot.Accuracy,
            Altitude = rawSnapshot.Altitude,
            Velocity = rawSnapshot.Velocity,
            Connection = rawSnapshot.Connection,
            TrackerId = rawSnapshot.TrackerId,
            Timestamp = rawSnapshot.Timestamp
        });

        return enriched;
    }

    public void SetCachedLocation(LocationInfo location)
    {
        _cachedLocation = location;
    }

    private void ApplyOwnTracksMetadata(LocationInfo location, OwnTracksMetadata? metadata)
    {
        if (metadata == null)
            return;

        location.BatteryLevel = metadata.BatteryLevel;
        location.BatteryStatus = metadata.BatteryStatus;
        location.Accuracy = metadata.Accuracy;
        location.Altitude = metadata.Altitude;
        location.Velocity = metadata.Velocity;
        location.Connection = metadata.Connection;
        location.TrackerId = metadata.TrackerId;
        location.Timestamp = metadata.Timestamp;

        if (location.MatchedKnownLocation == null)
        {
            location.HumanReadable = _humanReadableService.CreateHumanReadableText(location);
        }
        else
        {
            location.HumanReadable = _humanReadableService.CreateHumanReadableTextForKnownLocation(location);
        }
    }

    #endregion

    #region Known Locations Management

    /// <summary>
    /// Adds a known location with the specified parameters.
    /// </summary>
    private void AddKnownLocation(string name, string displayText, double latitude, double longitude, double radiusMeters = 100, string? icon = null)
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
    private KnownLocation? FindKnownLocation(double latitude, double longitude)
    {
        KnownLocation? closestMatch = null;
        double closestDistance = double.MaxValue;

        foreach (KnownLocation location in _knownLocations)
        {
            double distance = CalculateDistanceMeters(latitude, longitude, location.Latitude, location.Longitude);
            if ((distance > location.RadiusMeters) || (distance >= closestDistance)) continue;
            closestMatch = location;
            closestDistance = distance;
        }

        return closestMatch;
    }

    /// <summary>
    /// Calculates the distance between two coordinates using the Haversine formula.
    /// </summary>
    private static double CalculateDistanceMeters(double lat1, double lon1, double lat2, double lon2)
    {
        double dLat = DegreesToRadians(lat2 - lat1);
        double dLon = DegreesToRadians(lon2 - lon1);

        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                   Math.Cos(DegreesToRadians(lat1)) * Math.Cos(DegreesToRadians(lat2)) *
                   Math.Sin(dLon / 2) * Math.Sin(dLon / 2);

        double c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));

        return EarthRadiusMeters * c;
    }

    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180;

    #endregion

    #region Maps and QR Code Generation

    /// <summary>
    /// Generates a Google Maps URL for the given coordinates.
    /// </summary>
    private static string GenerateGoogleMapsUrl(double latitude, double longitude, string? label = null)
    {
        string baseUrl = "https://maps.google.com/maps";
        // Use invariant culture for coordinates to avoid locale decimal separator issues
        string coordinates = $"{latitude.ToString("F6", CultureInfo.InvariantCulture)},{longitude.ToString("F6", CultureInfo.InvariantCulture)}";
        
        if (!string.IsNullOrEmpty(label))
        {
            // Use the label as a query with coordinates
            string encodedLabel = Uri.EscapeDataString(label);
            return $"{baseUrl}?q={encodedLabel}@{coordinates}";
        }
        
        // Simple coordinate search
        return $"{baseUrl}?q={coordinates}";
    }

    /// <summary>
    /// Generates a QR code URL using the Google Charts API for the Google Maps URL.
    /// </summary>
    private static string GenerateQrCodeUrl(string googleMapsUrl, int size = 200)
    {
        string encodedUrl = Uri.EscapeDataString(googleMapsUrl);
        return $"https://chart.googleapis.com/chart?chs={size}x{size}&cht=qr&chl={encodedUrl}";
    }

    /// <summary>
    /// Generates a QR code URL directly from coordinates.
    /// </summary>
    public static string GenerateQrCodeForCoordinates(double latitude, double longitude, string? label = null, int qrSize = 200)
    {
        string mapsUrl = GenerateGoogleMapsUrl(latitude, longitude, label);
        return GenerateQrCodeUrl(mapsUrl, qrSize);
    }

    #endregion

    private async Task<LocationInfo?> GetLocationFromCoordinatesAsync(double latitude, double longitude)
    {
        // First check if this matches a known location
        KnownLocation? knownLocation = FindKnownLocation(latitude, longitude);
        
        try
        {
            // Format coordinates using invariant culture to ensure decimal point is used (not comma in some locales)
            string latStr = latitude.ToString("G", CultureInfo.InvariantCulture);
            string lonStr = longitude.ToString("G", CultureInfo.InvariantCulture);
            string url = $"{NominatimBaseUrl}?lat={latStr}&lon={lonStr}&format=json&addressdetails=1";

            HttpResponseMessage response = await _httpClient.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                // Read body for diagnostics (Nominatim often returns useful error details)
                string errorBody = await response.Content.ReadAsStringAsync();
                throw new HttpRequestException($"Nominatim API returned {(int)response.StatusCode} {response.ReasonPhrase}: {errorBody}");
            }
             
            string json = await response.Content.ReadAsStringAsync();
            NominatimResponse? nominatimResponse = JsonSerializer.Deserialize<NominatimResponse>(json);
            
            if (nominatimResponse == null)
                return null;

            NominatimAddress? address = nominatimResponse.Address;
            
            // Generate Google Maps URL and QR code
            string mapsLabel = knownLocation?.DisplayText ?? _humanReadableService.CreateHumanReadableText(address);
            string googleMapsUrl = GenerateGoogleMapsUrl(latitude, longitude, mapsLabel);
            string qrCodeUrl = GenerateQrCodeUrl(googleMapsUrl);
            
            LocationInfo locationInfo = new LocationInfo
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
                : _humanReadableService.CreateHumanReadableText(address, locationInfo);

            return locationInfo;
        }
        catch (Exception)
        {
            // If API fails but we have a known location, return that
            if (knownLocation != null)
            {
                string googleMapsUrl = GenerateGoogleMapsUrl(latitude, longitude, knownLocation.DisplayText);
                string qrCodeUrl = GenerateQrCodeUrl(googleMapsUrl);
                
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
        string? env = Environment.GetEnvironmentVariable("KNOWN_LOCATIONS");
        if (string.IsNullOrWhiteSpace(env))
            return;

        string[] entries = env.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (string rawEntry in entries)
        {
            string entry = rawEntry.Trim();
            if (string.IsNullOrEmpty(entry))
                continue;

            string[] parts = entry.Split('|', StringSplitOptions.TrimEntries);
            if (parts.Length < 4)
                continue; // need at least name|display|lat|lon

            if (!double.TryParse(parts[2], NumberStyles.Any, CultureInfo.InvariantCulture, out double lat))
                continue;
            if (!double.TryParse(parts[3], NumberStyles.Any, CultureInfo.InvariantCulture, out double lon))
                continue;

            string name = parts[0];
            string display = parts[1];
            double radius = 100.0;
            if (parts.Length >= 5 && double.TryParse(parts[4], NumberStyles.Any, CultureInfo.InvariantCulture, out double parsedRadius))
                radius = parsedRadius;

            AddKnownLocation(name, display, lat, lon, radius);
        }
    }
}
