# HomeLink

HomeLink is an ASP.NET Core Web API that renders a composite image for a **LilyGO T5 4.7″ e-ink display** (960×540, 1bpp). It combines your current Spotify playback with your live location from OwnTracks, draws the result using ImageSharp, and serves it as either raw packed bitmap bytes (for the device) or a PNG (for preview).

---

## Table of Contents

1. [How it Works](#how-it-works)
2. [Project Structure](#project-structure)
3. [Prerequisites](#prerequisites)
4. [Spotify Setup — Getting a Refresh Token](#spotify-setup--getting-a-refresh-token)
5. [OwnTracks Setup](#owntracks-setup)
6. [Configuration Reference](#configuration-reference)
7. [Running Locally](#running-locally)
8. [Docker](#docker)
9. [API Reference](#api-reference)
10. [Display Rendering Pipeline](#display-rendering-pipeline)
11. [Known Locations](#known-locations)
12. [Observability](#observability)
13. [Extending and Modifying](#extending-and-modifying)
14. [Troubleshooting](#troubleshooting)

---

## How it Works

```
OwnTracks app  ──POST /api/location/owntracks──►  LocationService
                                                       │ reverse-geocodes via Nominatim
                                                       │ matches against KnownLocations
                                                       ▼
LilyGO T5  ──GET /api/display/render──►  DisplayController
                                              │
                                   ┌──────────┴───────────┐
                                   ▼                       ▼
                             SpotifyService          LocationService
                          (currently-playing)      (cached location)
                                   └──────────┬───────────┘
                                              ▼
                                       DrawingService
                                    (compose, dither, pack)
                                              │
                                   ┌──────────┴───────────┐
                                   ▼                       ▼
                          raw 1-bpp bitmap             PNG preview
                      (application/octet-stream)     (image/png)
```

A background worker (`DisplayRenderWorker`) continuously pre-renders frames on a schedule (15 s while playing, 120 s while paused), so the device endpoint is a simple cache read and never blocks on rendering.

State (last location, last Spotify track) survives restarts via a local SQLite database at `state/homelink-state.db`.

---

## Project Structure

```
HomeLink/
├── Controllers/
│   ├── DisplayController.cs      — /api/display/* endpoints (render, image, test variants)
│   └── LocationController.cs     — /api/location/owntracks (OwnTracks webhook)
│
├── Models/
│   ├── ApiResponses.cs           — Response DTOs (RenderBitmapResponse, ErrorResponse, etc.)
│   ├── DisplayDataResponse.cs    — Structured display data model
│   ├── EInkBitmap.cs             — 1-bpp packed bitmap container
│   ├── KnownLocation.cs          — Named place definition (lat/lon/radius/icon)
│   ├── LocationInfo.cs           — Enriched location snapshot
│   ├── NominatimAddress.cs       — OpenStreetMap reverse-geocode result
│   ├── NominatimResponse.cs      — Nominatim API response wrapper
│   ├── OwnTracksMetadata.cs      — Device telemetry from OwnTracks (battery, speed, etc.)
│   └── SpotifyTrackInfo.cs       — Currently-playing track snapshot
│
├── Services/
│   ├── SpotifyService.cs         — Token refresh, currently-playing fetch, caching
│   ├── LocationService.cs        — OwnTracks ingestion, Nominatim geocoding, KnownLocation matching
│   ├── LocationEnrichmentQueue.cs — Async queue for background geocoding after raw ingest
│   ├── LocationEnrichmentWorker.cs— Background worker draining the enrichment queue
│   ├── StatePersistenceService.cs — SQLite read/write for location + Spotify state
│   ├── DrawingService.cs         — Main image composition pipeline (text, art, map, icons)
│   ├── DisplayDataService.cs     — Builds structured display data from service snapshots
│   ├── DisplayFrameCacheService.cs— Thread-safe cache for the latest pre-rendered frame
│   ├── DisplayRenderWorker.cs    — Background worker that polls and pre-renders frames
│   ├── DisplayFrameHashService.cs — Hashes render inputs for ETag / change detection
│   ├── HumanReadableService.cs   — Generates friendly location phrases (e.g. "Chilling at Home")
│   ├── IconDrawingService.cs     — Draws battery, wifi, and status icons onto the canvas
│   ├── ImageDitheringService.cs  — Floyd–Steinberg dithering for 1-bpp conversion
│   ├── MapTileService.cs         — Fetches OpenStreetMap tiles and composites a static map
│   └── QrCodeService.cs          — Renders QR code for the current location URL
│
├── Telemetry/
│   ├── HomeLinkTelemetry.cs      — OpenTelemetry ActivitySource + all custom meters/counters
│   ├── RuntimeTelemetrySampler.cs— Background sampler for runtime metrics (GC, CPU, memory)
│   ├── TelemetryDashboardPage.cs — Inline HTML for /telemetry/dashboard
│   └── TelemetryDashboardState.cs— In-memory aggregation of live metrics for the dashboard
│
├── Utils/
│   ├── GeoUtils.cs               — Tile coordinate math, Haversine distance
│   ├── TextUtils.cs              — Text truncation, city/country string builder
│   └── TimeUtils.cs              — Duration formatting helpers
│
├── Fonts/
│   ├── DejaVuSans.ttf            — Bundled font (regular)
│   └── DejaVuSans-Bold.ttf       — Bundled font (bold)
│
├── Program.cs                    — DI wiring, OpenTelemetry setup, middleware pipeline
├── appsettings.json              — Default config (logging, OTel service name)
├── appsettings.Development.json  — Development overrides (verbose logging, Spotify redirect URI)
├── Properties/launchSettings.json— Local run profiles (port 5119/7239)
├── Dockerfile                    — Multi-stage Docker build (SDK → runtime, listens on 8080)
└── HomeLink.http                 — HTTP request collection for manual testing
```

---

## Prerequisites

| Requirement | Notes |
|---|---|
| **.NET SDK 10.0** | `dotnet --version` should return `10.x.x`. Install from [dot.net](https://dotnet.microsoft.com/download). |
| **Spotify Developer App** | An **existing** app with `user-read-currently-playing` access is required. Spotify restricted new app access in 2024 — see [Spotify Setup](#spotify-setup--getting-a-refresh-token). |
| **OwnTracks** (optional) | iOS/Android app that POSTs location to your server. The display works without it but shows no location. |
| **Docker** (optional) | For containerised deployment. |

---

## Spotify Setup — Getting a Refresh Token

> **⚠️ Spotify API access is restricted (as of late 2024 onwards)**
>
> Spotify locked down their Web API for new apps. The `user-read-currently-playing` and `user-read-playback-state` scopes now require **Extended Quota Mode** approval before an app can be used in production. However, every app automatically gets **Development Mode**, which allows up to 25 manually-allowlisted Spotify accounts — more than enough for personal use.
>
> **TL;DR:** You still need an existing Spotify Developer App. If you don't have one, you will need to apply for extended access or use one of the alternative methods below.

---

### Prerequisites — Spotify Developer App

You need a Spotify Developer App with the `user-read-currently-playing` and `user-read-playback-state` scopes granted. If you already have one, skip to [Getting the Refresh Token](#getting-the-refresh-token).

1. Go to [developer.spotify.com/dashboard](https://developer.spotify.com/dashboard) and create an app (select **Web API** as the API type).
2. Note your **Client ID** and **Client Secret**.
3. In *Settings → User Management*, add your own Spotify account's email address to the allowlist. This is required in Development Mode.

---

### Getting the Refresh Token

Choose whichever method suits you. You only need to do this **once** — the app refreshes the token automatically at runtime.

---

#### Option A — Token Generator Tool (Easiest)

Use **[alecchen.dev/spotify-refresh-token](https://alecchen.dev/spotify-refresh-token/)** (or a similar hosted OAuth helper). It handles the browser redirect and token exchange for you without running any local server.

1. Open the tool in your browser.
2. Enter your **Client ID** and **Client Secret**.
3. Select the scopes: `user-read-currently-playing` and `user-read-playback-state`.
4. Click **Get Refresh Token** and log in with the Spotify account you added to the allowlist.
5. Copy the `refresh_token` from the result — that is your `SPOTIFY_REFRESH_TOKEN`.

> The tool runs entirely in your browser. Your credentials are never sent to the tool's server. If you'd rather not use a third-party site, use Option B or C.

---

#### Option B — Home Assistant (If you already run HA)

If you run [Home Assistant](https://www.home-assistant.io/), its built-in **Spotify** integration (Settings → Devices & Services → Add Integration → Spotify) performs the OAuth flow and maintains a token internally. You can extract the refresh token from HA's `.storage/auth.json` or by inspecting the integration's stored credentials — but the easier approach is to let HA act as the middle layer entirely and adapt HomeLink to call the HA REST API for now-playing state instead of Spotify directly.

This is an architectural change but avoids any Spotify credential management in HomeLink itself.

---

#### Option C — Manual PowerShell Exchange (No Third-Party Tools)

If you prefer to do everything yourself:

**Step 1 — Add a redirect URI to your Spotify app**

In your app's *Settings → Redirect URIs*, add:
```
http://localhost:5119/api/spotify/callback
```

**Step 2 — Open the authorization URL in your browser** (replace `YOUR_CLIENT_ID`):

```
https://accounts.spotify.com/authorize?client_id=YOUR_CLIENT_ID&response_type=code&redirect_uri=http%3A%2F%2Flocalhost%3A5119%2Fapi%2Fspotify%2Fcallback&scope=user-read-currently-playing+user-read-playback-state
```

After approving, Spotify redirects to:
```
http://localhost:5119/api/spotify/callback?code=AQD...
```

The page won't load (the app isn't running yet) but the `code` parameter is visible in the address bar — copy it.

**Step 3 — Exchange the code for a refresh token**

```powershell
$body  = "grant_type=authorization_code&code=<PASTE_CODE_HERE>&redirect_uri=http%3A%2F%2Flocalhost%3A5119%2Fapi%2Fspotify%2Fcallback"
$creds = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes("YOUR_CLIENT_ID:YOUR_CLIENT_SECRET"))

Invoke-RestMethod -Method Post `
  -Uri "https://accounts.spotify.com/api/token" `
  -Headers @{ Authorization = "Basic $creds" } `
  -ContentType "application/x-www-form-urlencoded" `
  -Body $body
```

The response JSON contains `refresh_token` — save it. This is your `SPOTIFY_REFRESH_TOKEN`.

> **Note:** Authorization codes expire in **10 minutes** and are single-use. If the exchange fails, go back to Step 2 and get a fresh code.

---

## OwnTracks Setup

[OwnTracks](https://github.com/owntracks/owntracks) is a free, open-source location-sharing app for iOS and Android. It POSTs your GPS coordinates to a URL of your choice — in this case, HomeLink's `/api/location/owntracks` endpoint. No cloud service is required; everything stays on your own infrastructure.

---

### 1. Install OwnTracks

| Platform | Store link |
|---|---|
| **Android** | [Google Play — OwnTracks](https://play.google.com/store/apps/details?id=org.owntracks.android) |
| **iOS** | [App Store — OwnTracks](https://apps.apple.com/app/owntracks/id692792motivate) |

> OwnTracks is also available as an [APK direct download](https://github.com/owntracks/android/releases) from its GitHub releases page if you prefer to sideload.

---

### 2. Choose a connection mode

OwnTracks supports two transport modes. **HTTP mode** is the simplest and is what HomeLink expects.

| Mode | How it works | When to use |
|---|---|---|
| **HTTP** | POSTs a JSON payload directly to your URL. | Recommended — simple, no broker needed. |
| **MQTT** | Publishes to an MQTT broker topic. | If you already run an MQTT broker (e.g. Mosquitto). |

HomeLink's `POST /api/location/owntracks` endpoint is compatible with the OwnTracks HTTP mode payload format.

---

### 3. Configure HTTP mode (recommended)

1. Open OwnTracks on your phone.
2. Tap the **⋮ menu → Preferences → Connection**.
3. Set **Mode** to **HTTP**.
4. Fill in the fields:

| Field | Value |
|---|---|
| **Host** | Your HomeLink server URL — e.g. `http://192.168.1.100:5119` or your public domain |
| **Path** | `/api/location/owntracks` |
| **Identification → Device ID** | Any short string, e.g. `phone` (becomes the `tid` field) |
| **Identification → Tracker ID** | 2-character ID shown on the display, e.g. `ph` |

5. Tap the **↑ upload** button (or move around to trigger a location publish) and watch HomeLink's logs for an incoming `OwnTracks location updated` message.

> **Tip:** If HomeLink is running behind a router, either forward port `5119` from your router to your server, or run it on a VPS/cloud VM with a public IP. OwnTracks on mobile data cannot reach a purely local IP unless you use a VPN (e.g. WireGuard, Tailscale).

---

### 4. Tune reporting frequency

In **Preferences → Reporting**, adjust how aggressively OwnTracks sends updates:

| Setting | Recommended value | Notes |
|---|---|---|
| **Monitoring mode** | *Significant* or *Move* | *Significant* saves battery; *Move* gives more frequent updates while moving. |
| **Ignore inaccurate locations** | `50` m or higher | Filters out low-quality GPS fixes. |
| **Locator interval** (Move mode) | `30`–`60` s | How often to check location in Move mode. |

The e-ink display refreshes on its own schedule (`DisplayRender:PlayingPollIntervalSeconds`), so there is no need to push more often than once every 30–60 seconds.

---

### 5. Verify the connection

After saving settings, trigger a manual publish:

- **Android:** tap **⋮ → Send location now**
- **iOS:** tap **↑ (publish)** on the map screen

Then confirm in HomeLink's logs:

```
info: LocationService - OwnTracks location updated: lat=48.2082, lon=16.3738
```

You can also call the `/api/display/image` endpoint in a browser to see the updated location reflected on the preview render.

---

### 6. (Optional) MQTT mode

If you prefer MQTT, run an MQTT broker (e.g. [Eclipse Mosquitto](https://mosquitto.org/)) and point OwnTracks at it. You will need to adapt HomeLink's `LocationController` to subscribe to the broker topic instead of receiving HTTP POSTs — this is an architectural change not covered here.

---

## Configuration Reference

### Environment Variables

| Variable | Required | Description |
|---|---|---|
| `SPOTIFY_REFRESH_TOKEN` | **Yes** | Refresh token from Spotify OAuth flow. |
| `SPOTIFY_ID` | For most apps | Spotify Client ID. Required to refresh tokens for confidential clients. |
| `SPOTIFY_SECRET` | For most apps | Spotify Client Secret. Required for confidential clients. |
| `KNOWN_LOCATIONS` | No | Semicolon-separated list of named places (see [Known Locations](#known-locations)). |

### appsettings.json Keys

| Key | Default | Description |
|---|---|---|
| `OpenTelemetry:ServiceName` | `HomeLink.Api` | Service name reported in traces. |
| `OpenTelemetry:Otlp:Endpoint` | *(empty)* | OTLP exporter endpoint. Empty → console exporter. |
| `DisplayRender:PlayingPollIntervalSeconds` | `15` | How often (seconds) to re-render while Spotify is playing. Range: 5–120. |
| `DisplayRender:PausedPollIntervalSeconds` | `120` | How often to re-render while Spotify is paused. Range: 15–600. |
| `DisplayRender:IdlePollIntervalSeconds` | `45` | How often to re-render when Spotify state is unknown. Range: 10–300. |
| `DisplayRender:PlayingCacheStalenessSeconds` | `PlayingPollInterval - 2` | Max age of Spotify cache before forcing a live fetch (playing). |
| `DisplayRender:PausedCacheStalenessSeconds` | `PausedPollInterval - 5` | Max age of Spotify cache before forcing a live fetch (paused). |

---

## Running Locally

### 1. Set environment variables

```powershell
$env:SPOTIFY_REFRESH_TOKEN = "<your-refresh-token>"
$env:SPOTIFY_ID            = "<your-client-id>"
$env:SPOTIFY_SECRET        = "<your-client-secret>"

# Optional — named places (see Known Locations section)
$env:KNOWN_LOCATIONS = "home|Home|48.2589|15.5557|100;work|Work|48.2031|16.3918|150"
```

### 2. Run the app

```powershell
dotnet run --project .\HomeLink\HomeLink.csproj
```

The app listens on **http://0.0.0.0:5119** and **https://localhost:7239** (as defined in `launchSettings.json`).

### 3. Send a test location

```powershell
Invoke-RestMethod -Method Post `
  -Uri "http://localhost:5119/api/location/owntracks" `
  -ContentType "application/json" `
  -Body '{ "_type": "location", "lat": 48.2082, "lon": 16.3738, "acc": 20, "tst": 1738070400 }'
```

### 4. Preview the display

Open `http://localhost:5119/api/display/image` in your browser to see a PNG preview of the current frame.

---

## Docker

The `Dockerfile` uses a multi-stage build. The container listens on port **8080**.

### Build

```powershell
# Run from the solution root (where HomeLink.sln lives)
docker build -t homelink:local -f HomeLink/Dockerfile .
```

### Run

```powershell
docker run --rm -p 5119:8080 `
  -e SPOTIFY_REFRESH_TOKEN="<your-refresh-token>" `
  -e SPOTIFY_ID="<your-client-id>" `
  -e SPOTIFY_SECRET="<your-client-secret>" `
  -e KNOWN_LOCATIONS="home|Home|48.2589|15.5557|100" `
  homelink:local
```

### Persistent state across restarts

The SQLite database is written inside the container at `/app/state/homelink-state.db`. Mount a volume to keep state across restarts:

```powershell
docker run --rm -p 5119:8080 `
  -v homelink-state:/app/state `
  -e SPOTIFY_REFRESH_TOKEN="<token>" `
  homelink:local
```

### With OTLP telemetry (e.g. Grafana / Jaeger on the host)

```powershell
docker run --rm -p 5119:8080 `
  -e SPOTIFY_REFRESH_TOKEN="<token>" `
  -e OpenTelemetry__Otlp__Endpoint="http://host.docker.internal:4317" `
  homelink:local
```

---

## API Reference

All endpoints are served from `http://localhost:5119` (or your configured URL).

### Display Endpoints — `/api/display`

#### `GET /api/display/render`

Returns the current pre-rendered 1-bpp frame as raw binary bytes (`application/octet-stream`). This is what the LilyGO T5 ESP32 firmware calls.

**Query parameters:**

| Parameter | Type | Default | Description |
|---|---|---|---|
| `dither` | bool | `true` | Apply Floyd–Steinberg dithering before packing. |
| `deviceBattery` | int? | — | Device battery % (0–100). Echoed in `X-Device-Battery` response header and shown on display. |

**Response headers:**

| Header | Description |
|---|---|
| `ETag` | Hash of the current frame content. Use with `If-None-Match` to get 304. |
| `X-Frame-Age-Ms` | How old the cached frame is in milliseconds. |
| `X-Width` / `X-Height` | Bitmap dimensions in pixels. |
| `X-BytesPerLine` | Packed bytes per row. |
| `X-Dithered` | Whether dithering was applied. |
| `X-Trace-Id` | OpenTelemetry trace ID for correlation. |

**Status codes:**

| Code | Meaning |
|---|---|
| `200 OK` | Binary bitmap body. |
| `304 Not Modified` | Frame unchanged since `If-None-Match` value. |
| `401 Unauthorized` | Spotify is not authorized (missing/invalid `SPOTIFY_REFRESH_TOKEN`). |
| `503 Service Unavailable` | Frame not yet ready — retry shortly (worker may still be initialising). |

---

#### `GET /api/display/image`

Returns a **PNG preview** of the current display rendering. Useful for testing layout in a browser without a physical device.

**Query parameters:** same as `/render` (`dither`, `deviceBattery`).

**Response:** `image/png`

> Unlike `/render`, this endpoint re-renders on every request (bypasses the frame cache). Use it for debugging, not for the device.

---

#### `GET /api/display/render-spotify-only`

Renders a frame using **only Spotify data** (no location). Returns JSON with a base64-encoded bitmap payload.

**Response:**
```json
{
  "success": true,
  "bitmap": {
    "width": 960,
    "height": 540,
    "bytesPerLine": 120,
    "data": "<base64>"
  }
}
```

---

#### `GET /api/display/render-location-only`

Renders a frame using **only the cached location** (no Spotify). Returns JSON with a base64-encoded bitmap.

Returns `404 Not Found` if no location has been cached yet.

---

### Location Endpoint — `/api/location`

#### `POST /api/location/owntracks`

OwnTracks-compatible webhook. Configure OwnTracks on your phone to POST to this URL.

**Request body** (OwnTracks JSON — only the listed fields are used, others are accepted and ignored):

```json
{
  "_type": "location",
  "lat": 48.2082,
  "lon": 16.3738,
  "acc": 20,
  "alt": 180,
  "batt": 85,
  "bs": 1,
  "vel": 0,
  "cog": 270,
  "tst": 1738070400,
  "tid": "ph",
  "t": "u",
  "conn": "w"
}
```

| Field | Description |
|---|---|
| `_type` | Must be `"location"` — other types are ignored. |
| `lat` / `lon` | Latitude / longitude (required). |
| `acc` | Accuracy in metres. |
| `batt` / `bs` | Battery % and status (0=unknown, 1=unplugged, 2=charging, 3=full). |
| `vel` | Speed in km/h — used by `HumanReadableService` for phrases like "Driving near …". |
| `tst` | Unix timestamp of the fix. |
| `conn` | Connection type: `w`=WiFi, `m`=mobile, `o`=offline. |

**Response:** `[]` (empty JSON array — required by the OwnTracks protocol).

Processing is split into two stages:
1. **Immediate** — raw coordinates are persisted to SQLite and the display worker is signalled.
2. **Background** — `LocationEnrichmentWorker` calls Nominatim for reverse-geocoding and matches against KnownLocations, then updates the cache.

---

### Telemetry Endpoints

#### `GET /telemetry/dashboard`

Live HTML dashboard showing request counts, error rates, render durations, and Spotify/location stats. No authentication required.

#### `GET /api/telemetry/summary`

JSON snapshot of the in-memory metrics used by the dashboard.

**Query parameters:**

| Parameter | Example | Description |
|---|---|---|
| `window` | `5m`, `1h`, `30s` | Time window to aggregate over. |
| `resolution` | `10s` | Bucket resolution for time-series data. |
| `maxPoints` | `60` | Max data points to return. |

---

### Health Check

#### `GET /health`

ASP.NET Core health check endpoint. Returns `200 OK` with body `Healthy` when the app is running.

---

## Display Rendering Pipeline

```
SpotifyService.GetCurrentlyPlayingAsync()
LocationService.GetCachedLocation()
        │
        ▼
DrawingService.DrawDisplayDataAsync(spotifyData, locationData, dither, deviceBattery)
        │
        ├─ Create 960×540 L8 (grayscale) canvas (white background)
        ├─ Draw Spotify section (left half)
        │    ├─ Fetch album art via HttpClient → resize → draw
        │    ├─ Draw track title, artist, album (DejaVuSans fonts)
        │    ├─ Draw progress bar
        │    ├─ Draw Spotify scannable QR code
        │    └─ Draw play/pause icon
        ├─ Draw location section (right half)
        │    ├─ MapTileService: fetch OSM tiles → composite → draw
        │    ├─ Draw crosshair on map center
        │    ├─ Draw human-readable location text
        │    ├─ Draw Google Maps QR code
        │    └─ IconDrawingService: battery + connection icons
        ├─ Draw battery indicator (if deviceBattery supplied)
        ├─ [if dither=true] ImageDitheringService: Floyd–Steinberg dithering
        └─ Pack pixels → EInkBitmap (1-bpp, row-major)
                │
                ▼
        DisplayFrameCacheService.UpdateFrame(...)
                │
        ┌───────┴────────┐
        ▼                ▼
  /render (binary)   /image (PNG)
```

### Display constants (in `DrawingService.cs`)

| Constant | Value | Change here to… |
|---|---|---|
| `DisplayWidth` | 960 | Support a different e-ink width |
| `DisplayHeight` | 540 | Support a different e-ink height |
| `Margin` | 20 | Adjust edge spacing |
| `AlbumArtSize` | 250 | Resize album art thumbnail |
| `QrCodeSize` | 120 | Resize QR codes |

---

## Known Locations

You can define named places that override the Nominatim geocoding result when the device is within range. This is useful for home, work, or frequently visited places.

### Format

Set the `KNOWN_LOCATIONS` environment variable:

```
name|DisplayText|latitude|longitude|radiusMeters
```

Multiple entries are separated by `;`. The `radiusMeters` field is optional (defaults to 100 m).

**Example:**
```
home|Home|48.25890085|15.555724704875526|100;work|Work|48.2030795|16.3918152|150;cafe|Favourite Café|48.1494469|16.2942323|50
```

### Icon field (optional sixth field)

A sixth `|`-separated field accepts an icon identifier string. Currently stored on the `KnownLocation` model but icon rendering is handled by `IconDrawingService` — extend that service to add new icon types.

### Matching behaviour

- The service finds the **closest** known location whose radius contains the current coordinates.
- If multiple overlap, the closest centre wins.
- If Nominatim fails but a known location matches, the known location is used as fallback.
- `HumanReadableService` generates context-aware phrases based on velocity:
  - Stationary → `"At Home"`, `"Chilling at Home"`, etc.
  - Walking speed → `"Walking near Home"`
  - Driving speed → `"Passing by Home"`

---

## Observability

HomeLink is fully instrumented with OpenTelemetry traces and metrics.

### Configuration

| Setting | Effect |
|---|---|
| `OpenTelemetry:Otlp:Endpoint` empty | Export to console (stdout). Good for local debugging. |
| `OpenTelemetry:Otlp:Endpoint=http://localhost:4317` | Export via gRPC to a local collector (Jaeger, Grafana, etc.). |
| `OpenTelemetry:Otlp:Endpoint=http://localhost:4318` | Export via HTTP/protobuf. |

### Custom metrics

| Metric | Type | Description |
|---|---|---|
| `homelink.display.render.requests` | Counter | Render requests received. |
| `homelink.display.render.duration` | Histogram (ms) | Time to serve a render request. |
| `homelink.location.updates` | Counter | OwnTracks updates processed. |
| `homelink.location.raw_ingest.duration` | Histogram (ms) | Time to cache a raw location. |
| `homelink.location.reverse_geocode.duration` | Histogram (ms) | Nominatim call duration. |
| `homelink.location.persistence.duration` | Histogram (ms) | SQLite write duration. |
| `homelink.spotify.requests` | Counter | Spotify currently-playing fetches. |
| `homelink.spotify.currently_playing.duration` | Histogram (ms) | Spotify API call duration. |
| `homelink.spotify.token_refresh.duration` | Histogram (ms) | Token refresh duration. |
| `homelink.spotify.snapshot_age` | Histogram (ms) | Age of Spotify data when consumed. |
| `homelink.drawing.stage.duration` | Histogram (ms) | Tagged per drawing pipeline stage. |
| `homelink.worker.queue.enqueue_to_start_lag` | Histogram (ms) | Location enrichment queue lag. |

Runtime metrics (GC, CPU, memory, thread pool) are collected automatically via `OpenTelemetry.Instrumentation.Runtime`.

Every HTTP response includes an `X-Trace-Id` header for correlating a specific request in your trace backend.

---

## Extending and Modifying

### Change the display layout

Edit `DrawingService.cs`. The main entry point is `DrawDisplayDataAsync`. The canvas is a 960×540 `Image<L8>` from ImageSharp.

- Left section (Spotify) starts at `x = Margin`.
- Right section (Location/Map) starts at roughly `x = DisplayWidth / 2`.
- Add new drawing calls using `SixLabors.ImageSharp.Drawing.Processing` extension methods.

### Add a new icon

Add your drawing logic to `IconDrawingService.cs`. Icons are drawn onto the main canvas using `DrawingOptions` with anti-aliasing disabled (`_noAaOptions`) to keep crisp 1-bpp output.

### Add a new known-location icon type

1. Add the icon identifier to `KnownLocation.Icon`.
2. Extend `IconDrawingService` with a new drawing method for that icon.
3. Call it from `DrawingService` when `locationData.MatchedKnownLocation?.Icon` matches.

### Change the Spotify data shown

Edit `SpotifyService.GetCurrentlyPlayingAsync()`. The method returns a `SpotifyTrackInfo` which is passed directly to `DrawingService`. Add new fields to `SpotifyTrackInfo` and wire them up in the drawing pipeline.

### Change the poll frequency

Set `DisplayRender:PlayingPollIntervalSeconds` / `DisplayRender:PausedPollIntervalSeconds` in `appsettings.json` or as environment variables (use `__` as separator for nested keys):

```
DisplayRender__PlayingPollIntervalSeconds=10
```

### Add a new API endpoint

1. Create or edit a controller in `Controllers/`.
2. Register any new services in `Program.cs`.
3. Services are resolved from DI — inject them via constructor parameters.

### Support a different e-ink display

1. Change `DisplayWidth` and `DisplayHeight` in `DrawingService.cs`.
2. If the display uses a different bit packing format, edit `EInkBitmap.cs` and the packing logic.
3. Update the firmware side accordingly.

### Switch to a different map provider

Replace the tile URL in `MapTileService.cs`:

```csharp
string tileUrl = $"https://tile.openstreetmap.org/{zoom}/{currentTileX}/{currentTileY}.png";
```

Any XYZ tile server works (Mapbox, Stadia, etc.) — just add your API key to the URL and update the `User-Agent` header if required by the provider.

---

## Troubleshooting

### `401 Unauthorized` from display endpoints

- Ensure `SPOTIFY_REFRESH_TOKEN` is set and is a valid, non-expired refresh token.
- Ensure `SPOTIFY_ID` and `SPOTIFY_SECRET` are set (required for most Spotify app types).
- Check application logs for `"Spotify token refresh"` errors.
- **Spotify API restriction:** If your Developer App was created after Spotify's 2024 API lockdown and has not been granted Extended Quota Mode, the `user-read-currently-playing` scope will be rejected for accounts not on the app's Development Mode allowlist. Add your Spotify account's email in the dashboard under *User Management*.

### `503 Service Unavailable` from `/api/display/render`

- The background `DisplayRenderWorker` hasn't completed its first render yet.
- Wait a few seconds after startup and retry.
- Check logs for `DisplayRenderWorker` errors.

### Location not appearing / showing "Locating…"

- Confirm you successfully POSTed an OwnTracks payload and the app logged `"OwnTracks location updated"`.
- The location text may show `"Locating…"` briefly while background geocoding is in progress — this updates automatically.
- If Nominatim is unreachable, the display will show raw coordinates.

### Map tiles not loading

- The app fetches tiles from `tile.openstreetmap.org`. Ensure outbound HTTP is allowed.
- OSM's tile usage policy requires a descriptive `User-Agent` — the app sends `"HomeLink/1.0 (E-Ink Display Application)"`. Respect their [usage policy](https://operations.osmfoundation.org/policies/tiles/).

### Spotify shows last track instead of current

- The worker caches Spotify state and only polls on the configured interval. Force a refresh by calling `GET /api/display/image` (this bypasses the cache and calls Spotify directly).
- If Spotify is temporarily unreachable, the last known track is shown with `IsPlaying = false`.

### App crashes on startup with font error

- The bundled DejaVuSans fonts are in `HomeLink/Fonts/` and are copied to the build output automatically (`CopyToOutputDirectory = PreserveNewest`).
- If running from a custom path, ensure the `Fonts/` directory exists next to the executable.

### Port conflicts

- Default ports: `5119` (HTTP), `7239` (HTTPS). Change them in `Properties/launchSettings.json` under `applicationUrl`, or pass `--urls` on the CLI:
  ```powershell
  dotnet run --project .\HomeLink\HomeLink.csproj --urls "http://0.0.0.0:8000"
  ```

### Telemetry / metrics not appearing in Grafana/Jaeger

- Set `OpenTelemetry:Otlp:Endpoint` to your collector's address (e.g. `http://localhost:4317`).
- In Docker, use `http://host.docker.internal:4317` to reach the host machine.
- Check that your collector is configured to accept the OTLP protocol (gRPC on 4317, HTTP on 4318).

---

## License

This project includes third-party libraries:

- [SpotifyAPI.Web](https://github.com/JohnnyCrazy/SpotifyAPI-NET) — MIT
- [SixLabors ImageSharp](https://github.com/SixLabors/ImageSharp) — Six Labors Split License
- [SixLabors ImageSharp.Drawing](https://github.com/SixLabors/ImageSharp.Drawing) — Six Labors Split License
- [QRCoder](https://github.com/codebude/QRCoder) — MIT
- [OpenTelemetry .NET](https://github.com/open-telemetry/opentelemetry-dotnet) — Apache 2.0

Consult each library's license before redistribution. The application code is intended for personal/home use.
