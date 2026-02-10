namespace HomeLink.Telemetry;

public static class TelemetryDashboardPage
{
    public static string Html => """
<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="UTF-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1.0" />
  <title>HomeLink Telemetry Dashboard</title>
  <style>
    body { font-family: Inter, Arial, sans-serif; margin: 0; background: #0f172a; color: #e2e8f0; }
    .wrap { max-width: 1100px; margin: 0 auto; padding: 24px; }
    h1 { margin: 0 0 8px; font-size: 1.6rem; }
    p { margin: 0 0 20px; color: #94a3b8; }
    .grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(260px, 1fr)); gap: 16px; }
    .card { background: #1e293b; border-radius: 12px; padding: 16px; box-shadow: 0 4px 16px rgba(0,0,0,0.25); }
    .title { margin: 0 0 12px; font-size: 1.05rem; }
    .metric { display: flex; justify-content: space-between; margin: 8px 0; color: #cbd5e1; }
    .value { color: #f8fafc; font-weight: 600; }
    .footer { margin-top: 16px; color: #94a3b8; font-size: .9rem; }
    .ok { color: #4ade80; }
    .bad { color: #f87171; }
  </style>
</head>
<body>
  <div class="wrap">
    <h1>HomeLink Telemetry Dashboard</h1>
    <p>Live app metrics from in-process counters (refreshes every 2s). For full telemetry pipelines, configure the OTLP exporter and inspect data in your collector/backend.</p>
    <div class="grid">
      <div class="card" id="display-card">
        <h2 class="title">Display Render</h2>
        <div class="metric"><span>Calls</span><span class="value" id="display-count">0</span></div>
        <div class="metric"><span>Errors</span><span class="value" id="display-errors">0</span></div>
        <div class="metric"><span>Error rate</span><span class="value" id="display-error-rate">0%</span></div>
        <div class="metric"><span>Last duration</span><span class="value" id="display-last">0 ms</span></div>
        <div class="metric"><span>Avg duration</span><span class="value" id="display-avg">0 ms</span></div>
      </div>

      <div class="card" id="location-card">
        <h2 class="title">Location Updates</h2>
        <div class="metric"><span>Calls</span><span class="value" id="location-count">0</span></div>
        <div class="metric"><span>Errors</span><span class="value" id="location-errors">0</span></div>
        <div class="metric"><span>Error rate</span><span class="value" id="location-error-rate">0%</span></div>
        <div class="metric"><span>Last duration</span><span class="value" id="location-last">0 ms</span></div>
        <div class="metric"><span>Avg duration</span><span class="value" id="location-avg">0 ms</span></div>
      </div>

      <div class="card" id="spotify-card">
        <h2 class="title">Spotify Calls</h2>
        <div class="metric"><span>Calls</span><span class="value" id="spotify-count">0</span></div>
        <div class="metric"><span>Errors</span><span class="value" id="spotify-errors">0</span></div>
        <div class="metric"><span>Error rate</span><span class="value" id="spotify-error-rate">0%</span></div>
        <div class="metric"><span>Last duration</span><span class="value" id="spotify-last">0 ms</span></div>
        <div class="metric"><span>Avg duration</span><span class="value" id="spotify-avg">0 ms</span></div>
      </div>
    </div>
    <div class="footer">Last updated: <span id="updated">never</span></div>
  </div>

  <script>
    function setSection(prefix, data) {
      document.getElementById(`${prefix}-count`).textContent = data.count;
      document.getElementById(`${prefix}-errors`).textContent = data.errors;
      document.getElementById(`${prefix}-error-rate`).textContent = `${data.errorRate}%`;
      document.getElementById(`${prefix}-last`).textContent = `${data.lastDurationMs} ms`;
      document.getElementById(`${prefix}-avg`).textContent = `${data.avgDurationMs} ms`;
      const rateElement = document.getElementById(`${prefix}-error-rate`);
      rateElement.classList.remove('ok','bad');
      rateElement.classList.add(data.errorRate < 5 ? 'ok' : 'bad');
    }

    async function refresh() {
      try {
        const response = await fetch('/api/telemetry/summary', { cache: 'no-store' });
        if (!response.ok) throw new Error(`HTTP ${response.status}`);
        const payload = await response.json();
        setSection('display', payload.display);
        setSection('location', payload.location);
        setSection('spotify', payload.spotify);
        document.getElementById('updated').textContent = new Date(payload.generatedAtUtc).toLocaleTimeString();
      } catch (error) {
        document.getElementById('updated').textContent = `refresh failed (${error.message})`;
      }
    }

    refresh();
    setInterval(refresh, 2000);
  </script>
</body>
</html>
""";
}
