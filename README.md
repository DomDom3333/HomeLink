# HomeLink

HomeLink is a small ASP.NET Core Web API that renders a composite image for a LilyGO T5 e‑ink display by combining your current Spotify playback and your latest location (from OwnTracks). It exposes endpoints to:
- Receive OwnTracks location updates and cache them.
- Query Spotify “currently playing” and render a 1‑bit packed bitmap tailored for the T5 display, or a PNG preview.

## Highlights
- Spotify integration via refresh-token flow using SpotifyAPI.Web.
- OwnTracks-compatible webhook to cache location and metadata.
- Rich drawing pipeline with dithering, layout, and album art fetch in `DrawingService`.
- Simple to run locally or in Docker; optional OpenAPI docs in Development.

## Project layout
- `Program.cs`: DI setup, reads Spotify env vars, configures controllers and OpenAPI.
- `Controllers/DisplayController.cs`: Renders display bitmap/PNG using services.
- `Controllers/LocationController.cs`: OwnTracks webhook (`/api/location/owntracks`) to cache location.
- `Services/SpotifyService.cs`: Handles token refresh and “currently playing” fetch.
- `Services/LocationService.cs`: Caches location, enriches with human-readable info.
- `Services/DrawingService.cs`: Composes the image (text, icons, album art) and outputs bitmap/PNG.
- `HomeLink.http`: Handy local request examples.

## Prerequisites
- .NET SDK 8 or 10 (project targets `net10.0` as seen in build output; .NET 8+ works with ASP.NET Core).
- Spotify Developer App (client id/secret if your app requires it).
- Optional: Docker (to build/run container).

## Configuration
HomeLink reads the following environment variables at startup:
- `SPOTIFY_REFRESH_TOKEN` (required): User refresh token used to obtain an access token.
- `SPOTIFY_ID` (optional): Spotify Client ID. Required for some app types.
- `SPOTIFY_SECRET` (optional): Spotify Client Secret. Required for confidential apps.
- `SPOTIFY_TOKEN_EXPIRY` (optional): Initial access-token expiry time. Accepts an ISO timestamp or unix seconds. If omitted, the app will refresh when needed.

Notes:
- If your app is a public client using PKCE, Spotify may not require `SPOTIFY_SECRET`. Confidential clients typically require both ID and SECRET to refresh.
- You can supply these via your shell, your IDE’s run configuration, or Docker environment.

## Running locally
Set the required environment variables, then run the app.

Windows PowerShell example:

```powershell
$env:SPOTIFY_REFRESH_TOKEN = "<your-refresh-token>"
$env:SPOTIFY_ID = "<your-client-id>"    # optional depending on app type
$env:SPOTIFY_SECRET = "<your-client-secret>" # optional depending on app type

# Run the web app
dotnet run --project .\HomeLink\HomeLink.csproj
```

By default ASP.NET Core selects a random port; check console output or use `launchSettings.json` profiles. The sample `HomeLink.http` uses `http://localhost:5119`.

## Docker
A `Dockerfile` is provided.

Build and run:
```powershell
# Build image
docker build -t homelink:local .

# Run container and pass Spotify secrets (only pass what your app type needs)
docker run --rm -p 5119:8080 `
  -e SPOTIFY_REFRESH_TOKEN="<your-refresh-token>" `
  -e SPOTIFY_ID="<your-client-id>" `
  -e SPOTIFY_SECRET="<your-client-secret>" `
  homelink:local
```
Note: The container listens on 8080; we map to 5119 externally for consistency with `HomeLink.http`.

## Observability (OpenTelemetry)
HomeLink is instrumented end-to-end with OpenTelemetry for traces and metrics.

What is captured:
- Incoming HTTP request traces (ASP.NET Core instrumentation).
- Outgoing HTTP calls (Spotify + Nominatim/map fetches via HttpClient instrumentation).
- Runtime + process metrics (GC, CPU, memory, threads).
- Custom HomeLink metrics:
  - `homelink.display.render.requests`
  - `homelink.display.render.duration` (ms)
  - `homelink.location.updates`
  - `homelink.location.lookup.duration` (ms)
  - `homelink.spotify.requests`
  - `homelink.spotify.currently_playing.duration` (ms)
- Correlation header: each response includes `X-Trace-Id`.

Configuration:
- `OpenTelemetry:ServiceName` (default `HomeLink.Api`)
- `OpenTelemetry:Otlp:Endpoint` (when set, OTLP exporter is used for traces + metrics)
- If OTLP endpoint is empty, telemetry is exported to console.

Example OTLP endpoint values:
- `http://localhost:4317` (gRPC)
- `http://localhost:4318` (HTTP/protobuf)

For Docker:
```powershell
docker run --rm -p 5119:8080 `
  -e SPOTIFY_REFRESH_TOKEN="<your-refresh-token>" `
  -e OpenTelemetry__Otlp__Endpoint="http://host.docker.internal:4317" `
  homelink:local
```

## API endpoints
Unless noted, all endpoints are under the root URL (e.g., `http://localhost:5119`).

DisplayController (`/api/display`):
- `GET /api/display/render`
  - Returns JSON with bitmap payload for T5 e‑ink display.
  - Requires Spotify to be authorized; uses cached location from OwnTracks.
  - Response shape:
    - `success`: boolean
    - `bitmap`: `{ width, height, bytesPerLine, data }`, where `data` is Base64 of 1‑bit packed bytes.
- `GET /api/display/image`
  - Returns a PNG of the same rendering for quick preview/testing.

LocationController (`/api/location`):
- `POST /api/location/owntracks`
  - OwnTracks webhook for location updates. Accepts OwnTracks JSON payload (type `location`).
  - Caches latest location + metadata for use by the display endpoints.
  - Returns `[]` (empty array) for protocol compatibility when successful.

Additional convenience requests (see `HomeLink.http`):
- `GET /api/display/render-spotify-only` — renders only Spotify section (if present in your current build/branch).
- `GET /api/display/render-location-only?latitude=48.2082&longitude=16.3738` — render with explicit coords for testing (if present).
- `GET /api/spotify/authorize`, `GET /api/spotify/status`, `GET /api/spotify/info` — helper endpoints may exist in some branches to guide OAuth; availability depends on your current code.

## Data flow
1. OwnTracks posts a `location` payload to `/api/location/owntracks`. `LocationService` caches the coordinates and metadata (accuracy, altitude, battery, etc.).
2. When you request `/api/display/render` or `/api/display/image`, the app fetches your Spotify “currently playing” using `SpotifyService` and combines it with the cached location.
3. `DrawingService` composes text, icons, album art, and other accents, applies dithering, and returns either:
   - A 1‑bit packed bitmap (base64) for embedded devices, or
   - A PNG for quick preview.

## Local testing
Use the bundled `HomeLink.http` file with an HTTP client (JetBrains Rider, VS Code REST Client, or Postman):
- Update `@HomeLink_HostAddress` at the top to match your local port.
- Run requests in order:
  1. Send an OwnTracks payload to cache a location:
     ```json
     {
       "_type": "location",
       "lat": 48.2082,
       "lon": 16.3738,
       "acc": 20,
       "tst": 1738070400
     }
     ```
     POST to `/api/location/owntracks`.
  2. Hit `GET /api/display/image` to preview the composed PNG.
  3. Hit `GET /api/display/render` to obtain the device bitmap.

## Troubleshooting
- 401 Unauthorized from display endpoints
  - Ensure `SPOTIFY_REFRESH_TOKEN` is set and valid.
  - If Spotify requires client authentication for token refresh, also set `SPOTIFY_ID` and `SPOTIFY_SECRET`.
- Empty or paused Spotify info
  - If nothing is currently playing, the app returns the last known track marked as `IsPlaying = false`.
- No location appearing on display
  - Verify you sent an OwnTracks `location` message and the app logged a cached location update.
- Port/URL issues
  - Confirm the actual listening URL printed by ASP.NET Core. Adjust `HomeLink.http` base address accordingly.
- Docker networking
  - Map container port 8080 to a host port and call the host port (e.g., `-p 5119:8080`).

## Development notes
- In Development environment, OpenAPI is mapped (`app.MapOpenApi()`), so you can browse `/openapi` or the generated JSON depending on tooling.
- Services are registered as:
  - `SpotifyService`: singleton (holds tokens/cache).
  - `LocationService`: singleton + typed HttpClient.
  - `DrawingService`: scoped + typed HttpClient.

## License
This repository contains third-party libraries (e.g., SpotifyAPI.Web, SixLabors ImageSharp, QRCoder). Consult their respective licenses. The application code is intended for personal/home use; add an explicit license if you plan to distribute.
