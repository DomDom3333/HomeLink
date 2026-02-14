using System.Globalization;
using HomeLink.Models;
using Microsoft.Data.Sqlite;

namespace HomeLink.Services;

public class StatePersistenceService
{
    private readonly ILogger<StatePersistenceService> _logger;
    private readonly string _connectionString;
    private readonly SemaphoreSlim _dbLock = new(1, 1);

    public StatePersistenceService(ILogger<StatePersistenceService> logger)
    {
        _logger = logger;

        string stateDirectory = Path.Combine(AppContext.BaseDirectory, "state");
        Directory.CreateDirectory(stateDirectory);

        string dbPath = Path.Combine(stateDirectory, "homelink-state.db");
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true
        }.ToString();

        InitializeDatabase();
    }

    public async Task SaveLocationAsync(LocationInfo location)
    {
        await _dbLock.WaitAsync();
        try
        {
            await using SqliteConnection connection = new(_connectionString);
            await connection.OpenAsync();

            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = @"
                INSERT INTO location_state (
                    id, latitude, longitude, display_name, human_readable,
                    district, city, town, village, country,
                    matched_known_location_name, matched_known_location_display_text,
                    matched_known_location_latitude, matched_known_location_longitude,
                    matched_known_location_radius_meters, matched_known_location_icon,
                    google_maps_url, qr_code_url,
                    battery_level, battery_status, accuracy, altitude, velocity,
                    connection, tracker_id, location_timestamp
                ) VALUES (
                    1, $latitude, $longitude, $displayName, $humanReadable,
                    $district, $city, $town, $village, $country,
                    $matchedKnownLocationName, $matchedKnownLocationDisplayText,
                    $matchedKnownLocationLatitude, $matchedKnownLocationLongitude,
                    $matchedKnownLocationRadiusMeters, $matchedKnownLocationIcon,
                    $googleMapsUrl, $qrCodeUrl,
                    $batteryLevel, $batteryStatus, $accuracy, $altitude, $velocity,
                    $connection, $trackerId, $locationTimestamp
                )
                ON CONFLICT(id) DO UPDATE SET
                    latitude = excluded.latitude,
                    longitude = excluded.longitude,
                    display_name = excluded.display_name,
                    human_readable = excluded.human_readable,
                    district = excluded.district,
                    city = excluded.city,
                    town = excluded.town,
                    village = excluded.village,
                    country = excluded.country,
                    matched_known_location_name = excluded.matched_known_location_name,
                    matched_known_location_display_text = excluded.matched_known_location_display_text,
                    matched_known_location_latitude = excluded.matched_known_location_latitude,
                    matched_known_location_longitude = excluded.matched_known_location_longitude,
                    matched_known_location_radius_meters = excluded.matched_known_location_radius_meters,
                    matched_known_location_icon = excluded.matched_known_location_icon,
                    google_maps_url = excluded.google_maps_url,
                    qr_code_url = excluded.qr_code_url,
                    battery_level = excluded.battery_level,
                    battery_status = excluded.battery_status,
                    accuracy = excluded.accuracy,
                    altitude = excluded.altitude,
                    velocity = excluded.velocity,
                    connection = excluded.connection,
                    tracker_id = excluded.tracker_id,
                    location_timestamp = excluded.location_timestamp,
                    updated_utc = strftime('%Y-%m-%dT%H:%M:%fZ', 'now');";

            AddLocationParameters(command, location);
            await command.ExecuteNonQueryAsync();
        }
        finally
        {
            _dbLock.Release();
        }
    }

    public async Task<LocationInfo?> LoadLocationAsync()
    {
        await _dbLock.WaitAsync();
        try
        {
            await using SqliteConnection connection = new(_connectionString);
            await connection.OpenAsync();

            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """

                                                  SELECT
                                                      latitude, longitude, display_name, human_readable,
                                                      district, city, town, village, country,
                                                      matched_known_location_name, matched_known_location_display_text,
                                                      matched_known_location_latitude, matched_known_location_longitude,
                                                      matched_known_location_radius_meters, matched_known_location_icon,
                                                      google_maps_url, qr_code_url,
                                                      battery_level, battery_status, accuracy, altitude, velocity,
                                                      connection, tracker_id, location_timestamp
                                                  FROM location_state
                                                  WHERE id = 1;
                                  """;

            await using SqliteDataReader reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
                return null;

            KnownLocation? knownLocation = null;
            if (!await reader.IsDBNullAsync(9) && !await reader.IsDBNullAsync(10) && !await reader.IsDBNullAsync(11) && !await reader.IsDBNullAsync(12))
            {
                knownLocation = new KnownLocation(
                    reader.GetString(9),
                    reader.GetString(10),
                    reader.GetDouble(11),
                    reader.GetDouble(12),
                    await reader.IsDBNullAsync(13) ? 100 : reader.GetDouble(13),
                    await reader.IsDBNullAsync(14) ? null : reader.GetString(14));
            }

            return new LocationInfo
            {
                Latitude = reader.GetDouble(0),
                Longitude = reader.GetDouble(1),
                DisplayName = await reader.IsDBNullAsync(2) ? string.Empty : reader.GetString(2),
                HumanReadable = await reader.IsDBNullAsync(3) ? string.Empty : reader.GetString(3),
                District = await reader.IsDBNullAsync(4) ? null : reader.GetString(4),
                City = await reader.IsDBNullAsync(5) ? null : reader.GetString(5),
                Town = await reader.IsDBNullAsync(6) ? null : reader.GetString(6),
                Village = await reader.IsDBNullAsync(7) ? null : reader.GetString(7),
                Country = await reader.IsDBNullAsync(8) ? null : reader.GetString(8),
                MatchedKnownLocation = knownLocation,
                GoogleMapsUrl = await reader.IsDBNullAsync(15) ? string.Empty : reader.GetString(15),
                QrCodeUrl = await reader.IsDBNullAsync(16) ? string.Empty : reader.GetString(16),
                BatteryLevel = await reader.IsDBNullAsync(17) ? null : reader.GetInt32(17),
                BatteryStatus = await reader.IsDBNullAsync(18) ? null : reader.GetInt32(18),
                Accuracy = await reader.IsDBNullAsync(19) ? null : reader.GetInt32(19),
                Altitude = await reader.IsDBNullAsync(20) ? null : reader.GetInt32(20),
                Velocity = await reader.IsDBNullAsync(21) ? null : reader.GetInt32(21),
                Connection = await reader.IsDBNullAsync(22) ? null : reader.GetString(22),
                TrackerId = await reader.IsDBNullAsync(23) ? null : reader.GetString(23),
                Timestamp = await reader.IsDBNullAsync(24) ? null : reader.GetInt64(24)
            };
        }
        finally
        {
            _dbLock.Release();
        }
    }

    public async Task SaveSpotifyTrackAsync(SpotifyTrackInfo trackInfo, DateTime lastSyncUtc)
    {
        await _dbLock.WaitAsync();
        try
        {
            await using SqliteConnection connection = new(_connectionString);
            await connection.OpenAsync();

            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """

                                                  INSERT INTO spotify_state (
                                                      id, title, artist, album, album_cover_url,
                                                      progress_ms, duration_ms, spotify_uri, scannable_code_url,
                                                      is_playing, last_sync_utc
                                                  ) VALUES (
                                                      1, $title, $artist, $album, $albumCoverUrl,
                                                      $progressMs, $durationMs, $spotifyUri, $scannableCodeUrl,
                                                      $isPlaying, $lastSyncUtc
                                                  )
                                                  ON CONFLICT(id) DO UPDATE SET
                                                      title = excluded.title,
                                                      artist = excluded.artist,
                                                      album = excluded.album,
                                                      album_cover_url = excluded.album_cover_url,
                                                      progress_ms = excluded.progress_ms,
                                                      duration_ms = excluded.duration_ms,
                                                      spotify_uri = excluded.spotify_uri,
                                                      scannable_code_url = excluded.scannable_code_url,
                                                      is_playing = excluded.is_playing,
                                                      last_sync_utc = excluded.last_sync_utc,
                                                      updated_utc = strftime('%Y-%m-%dT%H:%M:%fZ', 'now');
                                  """;

            command.Parameters.AddWithValue("$title", trackInfo.Title);
            command.Parameters.AddWithValue("$artist", trackInfo.Artist);
            command.Parameters.AddWithValue("$album", trackInfo.Album);
            command.Parameters.AddWithValue("$albumCoverUrl", trackInfo.AlbumCoverUrl);
            command.Parameters.AddWithValue("$progressMs", trackInfo.ProgressMs);
            command.Parameters.AddWithValue("$durationMs", trackInfo.DurationMs);
            command.Parameters.AddWithValue("$spotifyUri", trackInfo.SpotifyUri);
            command.Parameters.AddWithValue("$scannableCodeUrl", trackInfo.ScannableCodeUrl);
            command.Parameters.AddWithValue("$isPlaying", trackInfo.IsPlaying ? 1 : 0);
            command.Parameters.AddWithValue("$lastSyncUtc", lastSyncUtc.ToString("O", CultureInfo.InvariantCulture));
            await command.ExecuteNonQueryAsync();
        }
        finally
        {
            _dbLock.Release();
        }
    }

    public async Task<(SpotifyTrackInfo TrackInfo, DateTime LastSyncUtc)?> LoadSpotifyTrackAsync()
    {
        await _dbLock.WaitAsync();
        try
        {
            await using SqliteConnection connection = new(_connectionString);
            await connection.OpenAsync();

            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = @"
                SELECT
                    title, artist, album, album_cover_url,
                    progress_ms, duration_ms, spotify_uri, scannable_code_url,
                    is_playing, last_sync_utc
                FROM spotify_state
                WHERE id = 1;";

            await using SqliteDataReader reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
                return null;

            string lastSyncRaw = await reader.IsDBNullAsync(9) ? string.Empty : reader.GetString(9);
            DateTime lastSyncUtc = DateTime.MinValue;
            if (!string.IsNullOrWhiteSpace(lastSyncRaw) &&
                DateTime.TryParse(lastSyncRaw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime parsedLastSync))
            {
                lastSyncUtc = parsedLastSync;
            }

            SpotifyTrackInfo trackInfo = new()
            {
                Title = await reader.IsDBNullAsync(0) ? string.Empty : reader.GetString(0),
                Artist = await reader.IsDBNullAsync(1) ? string.Empty : reader.GetString(1),
                Album = await reader.IsDBNullAsync(2) ? string.Empty : reader.GetString(2),
                AlbumCoverUrl = await reader.IsDBNullAsync(3) ? string.Empty : reader.GetString(3),
                ProgressMs = await reader.IsDBNullAsync(4) ? 0 : reader.GetInt64(4),
                DurationMs = await reader.IsDBNullAsync(5) ? 0 : reader.GetInt64(5),
                SpotifyUri = await reader.IsDBNullAsync(6) ? string.Empty : reader.GetString(6),
                ScannableCodeUrl = await reader.IsDBNullAsync(7) ? string.Empty : reader.GetString(7),
                IsPlaying = !await reader.IsDBNullAsync(8) && reader.GetInt64(8) == 1
            };

            return (trackInfo, lastSyncUtc);
        }
        finally
        {
            _dbLock.Release();
        }
    }

    private static void AddLocationParameters(SqliteCommand command, LocationInfo location)
    {
        command.Parameters.AddWithValue("$latitude", location.Latitude);
        command.Parameters.AddWithValue("$longitude", location.Longitude);
        command.Parameters.AddWithValue("$displayName", location.DisplayName);
        command.Parameters.AddWithValue("$humanReadable", location.HumanReadable);
        command.Parameters.AddWithValue("$district", (object?)location.District ?? DBNull.Value);
        command.Parameters.AddWithValue("$city", (object?)location.City ?? DBNull.Value);
        command.Parameters.AddWithValue("$town", (object?)location.Town ?? DBNull.Value);
        command.Parameters.AddWithValue("$village", (object?)location.Village ?? DBNull.Value);
        command.Parameters.AddWithValue("$country", (object?)location.Country ?? DBNull.Value);

        command.Parameters.AddWithValue("$matchedKnownLocationName", (object?)location.MatchedKnownLocation?.Name ?? DBNull.Value);
        command.Parameters.AddWithValue("$matchedKnownLocationDisplayText", (object?)location.MatchedKnownLocation?.DisplayText ?? DBNull.Value);
        command.Parameters.AddWithValue("$matchedKnownLocationLatitude", location.MatchedKnownLocation is null ? DBNull.Value : location.MatchedKnownLocation.Latitude);
        command.Parameters.AddWithValue("$matchedKnownLocationLongitude", location.MatchedKnownLocation is null ? DBNull.Value : location.MatchedKnownLocation.Longitude);
        command.Parameters.AddWithValue("$matchedKnownLocationRadiusMeters", location.MatchedKnownLocation is null ? DBNull.Value : location.MatchedKnownLocation.RadiusMeters);
        command.Parameters.AddWithValue("$matchedKnownLocationIcon", (object?)location.MatchedKnownLocation?.Icon ?? DBNull.Value);

        command.Parameters.AddWithValue("$googleMapsUrl", location.GoogleMapsUrl);
        command.Parameters.AddWithValue("$qrCodeUrl", location.QrCodeUrl);
        command.Parameters.AddWithValue("$batteryLevel", (object?)location.BatteryLevel ?? DBNull.Value);
        command.Parameters.AddWithValue("$batteryStatus", (object?)location.BatteryStatus ?? DBNull.Value);
        command.Parameters.AddWithValue("$accuracy", (object?)location.Accuracy ?? DBNull.Value);
        command.Parameters.AddWithValue("$altitude", (object?)location.Altitude ?? DBNull.Value);
        command.Parameters.AddWithValue("$velocity", (object?)location.Velocity ?? DBNull.Value);
        command.Parameters.AddWithValue("$connection", (object?)location.Connection ?? DBNull.Value);
        command.Parameters.AddWithValue("$trackerId", (object?)location.TrackerId ?? DBNull.Value);
        command.Parameters.AddWithValue("$locationTimestamp", (object?)location.Timestamp ?? DBNull.Value);
    }

    private void InitializeDatabase()
    {
        using SqliteConnection connection = new(_connectionString);
        connection.Open();

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = @"
            PRAGMA journal_mode = WAL;

            CREATE TABLE IF NOT EXISTS location_state (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                latitude REAL NOT NULL,
                longitude REAL NOT NULL,
                display_name TEXT NOT NULL,
                human_readable TEXT NOT NULL,
                district TEXT NULL,
                city TEXT NULL,
                town TEXT NULL,
                village TEXT NULL,
                country TEXT NULL,
                matched_known_location_name TEXT NULL,
                matched_known_location_display_text TEXT NULL,
                matched_known_location_latitude REAL NULL,
                matched_known_location_longitude REAL NULL,
                matched_known_location_radius_meters REAL NULL,
                matched_known_location_icon TEXT NULL,
                google_maps_url TEXT NOT NULL,
                qr_code_url TEXT NOT NULL,
                battery_level INTEGER NULL,
                battery_status INTEGER NULL,
                accuracy INTEGER NULL,
                altitude INTEGER NULL,
                velocity INTEGER NULL,
                connection TEXT NULL,
                tracker_id TEXT NULL,
                location_timestamp INTEGER NULL,
                updated_utc TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
            );

            CREATE TABLE IF NOT EXISTS spotify_state (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                title TEXT NOT NULL,
                artist TEXT NOT NULL,
                album TEXT NOT NULL,
                album_cover_url TEXT NOT NULL,
                progress_ms INTEGER NOT NULL,
                duration_ms INTEGER NOT NULL,
                spotify_uri TEXT NOT NULL,
                scannable_code_url TEXT NOT NULL,
                is_playing INTEGER NOT NULL,
                last_sync_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
            );";

        command.ExecuteNonQuery();
        _logger.LogInformation("State persistence initialized at {DataSource}", new SqliteConnectionStringBuilder(_connectionString).DataSource);
    }
}
