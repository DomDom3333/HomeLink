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
    .metric { display: flex; justify-content: space-between; margin: 8px 0; color: #cbd5e1; gap: 8px; }
    .value { color: #f8fafc; font-weight: 600; text-align: right; }
    .footer { margin-top: 16px; color: #94a3b8; font-size: .9rem; }
    .ok { color: #4ade80; }
    .bad { color: #f87171; }
    .history { margin-top: 10px; max-height: 180px; overflow-y: auto; border-top: 1px solid rgba(148, 163, 184, 0.25); padding-top: 8px; }
    .history-row { display: flex; justify-content: space-between; font-size: .85rem; color: #cbd5e1; margin: 4px 0; }
  </style>
</head>
<body>
  <div class="wrap">
    <h1>HomeLink Telemetry Dashboard</h1>
    <p>Live app metrics from in-process counters (refreshes every 2s). Includes request-rate windows and latest device battery telemetry from /api/display/render calls.</p>
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

      <div class="card" id="device-card">
        <h2 class="title">Client Device</h2>
        <div class="metric"><span>Latest battery</span><span class="value" id="device-battery">n/a</span></div>
        <div class="metric"><span>Predicted battery (1h)</span><span class="value" id="device-battery-prediction">n/a</span></div>
        <div class="metric"><span>Time until empty</span><span class="value" id="device-time-to-empty">n/a</span></div>
        <div class="metric"><span>Req last hour</span><span class="value" id="device-last-hour">0</span></div>
        <div class="metric"><span>Req last day</span><span class="value" id="device-last-day">0</span></div>
        <div class="metric"><span>Avg req / hour (1d)</span><span class="value" id="device-avg-hour">0</span></div>
        <div class="metric"><span>Avg req / day (1d)</span><span class="value" id="device-avg-day">0</span></div>
        <div class="history" id="device-history"></div>
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

    function formatPercent(value) {
      return value === null || value === undefined ? 'n/a' : `${value}%`;
    }

    function formatHours(value) {
      if (value === null || value === undefined) return 'n/a';
      if (value >= 24) return `${(value / 24).toFixed(1)} d`;
      return `${value.toFixed(1)} h`;
    }

    function setDevice(data) {
      document.getElementById('device-battery').textContent = formatPercent(data.latestBatteryPercent);
      document.getElementById('device-battery-prediction').textContent = formatPercent(data.latestPredictedBatteryPercent);
      document.getElementById('device-time-to-empty').textContent = formatHours(data.latestPredictedHoursToEmpty);
      document.getElementById('device-last-hour').textContent = data.requestsLastHour;
      document.getElementById('device-last-day').textContent = data.requestsLastDay;
      document.getElementById('device-avg-hour').textContent = data.avgRequestsPerHour;
      document.getElementById('device-avg-day').textContent = data.avgRequestsPerDay;

      const history = document.getElementById('device-history');
      history.innerHTML = '';
      const rows = (data.batteryHistory ?? []).slice(-12).reverse();

      if (rows.length === 0) {
        history.textContent = 'No battery samples yet.';
        return;
      }

      for (const entry of rows) {
        const row = document.createElement('div');
        row.className = 'history-row';
        const left = document.createElement('span');
        left.textContent = new Date(entry.timestampUtc).toLocaleTimeString();
        const right = document.createElement('span');
        right.textContent = `${formatPercent(entry.batteryPercent)} → ${formatPercent(entry.predictedBatteryPercent)} (${formatHours(entry.predictedHoursToEmpty)})`;
        row.appendChild(left);
        row.appendChild(right);
        history.appendChild(row);
      }
    }

    async function refresh() {
      try {
        const response = await fetch('/api/telemetry/summary', { cache: 'no-store' });
        if (!response.ok) throw new Error(`HTTP ${response.status}`);
        const payload = await response.json();
        setSection('display', payload.display);
        setSection('location', payload.location);
        setSection('spotify', payload.spotify);
        setDevice(payload.device);
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
