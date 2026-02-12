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
    .wrap { max-width: 1180px; margin: 0 auto; padding: 24px; }
    h1 { margin: 0 0 8px; font-size: 1.6rem; }
    p { margin: 0 0 16px; color: #94a3b8; }
    .controls { display: flex; flex-wrap: wrap; align-items: center; justify-content: space-between; gap: 14px; margin: 0 0 18px; }
    .range-group { display: flex; flex-wrap: wrap; gap: 8px; }
    .range-btn { border: 1px solid rgba(148, 163, 184, 0.35); background: #1e293b; color: #cbd5e1; border-radius: 999px; padding: 6px 12px; font-size: .85rem; cursor: pointer; }
    .range-btn.active { background: #2563eb; border-color: #2563eb; color: #f8fafc; }
    .refresh-group { display: flex; align-items: center; gap: 8px; color: #cbd5e1; font-size: .9rem; }
    .refresh-select { background: #1e293b; color: #e2e8f0; border: 1px solid rgba(148, 163, 184, 0.35); border-radius: 8px; padding: 6px 8px; }
    .grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(260px, 1fr)); gap: 16px; }
    .card { background: #1e293b; border-radius: 12px; padding: 16px; box-shadow: 0 4px 16px rgba(0,0,0,0.25); }
    .chart-card { grid-column: 1 / -1; }
    .title { margin: 0 0 12px; font-size: 1.05rem; }
    .metric { display: flex; justify-content: space-between; margin: 8px 0; color: #cbd5e1; gap: 8px; }
    .value { color: #f8fafc; font-weight: 600; text-align: right; }
    .footer { margin-top: 16px; color: #94a3b8; font-size: .9rem; }
    .ok { color: #4ade80; }
    .bad { color: #f87171; }
    .history { margin-top: 10px; max-height: 180px; overflow-y: auto; border-top: 1px solid rgba(148, 163, 184, 0.25); padding-top: 8px; }
    .history-row { display: flex; justify-content: space-between; font-size: .85rem; color: #cbd5e1; margin: 4px 0; }
    .hint { font-size: .75rem; color: #94a3b8; margin-top: 6px; line-height: 1.3; }
    .chart-meta { display: flex; justify-content: space-between; align-items: center; gap: 12px; margin-bottom: 10px; color: #94a3b8; font-size: .8rem; }
    .chart-legend { display: flex; flex-wrap: wrap; gap: 12px; }
    .legend-item { display: inline-flex; align-items: center; gap: 6px; }
    .legend-swatch { width: 10px; height: 10px; border-radius: 50%; display: inline-block; }
    .chart { width: 100%; height: 220px; display: block; background: rgba(15, 23, 42, 0.5); border: 1px solid rgba(148, 163, 184, 0.2); border-radius: 10px; }
    .chart-empty { margin: 0; color: #94a3b8; font-size: .85rem; }
  </style>
</head>
<body>
  <div class="wrap">
    <h1>HomeLink Telemetry Dashboard</h1>
    <p>Live app metrics from in-process counters. Use range and refresh controls to tune chart windows without reloading the page.</p>

    <div class="controls">
      <div class="range-group" role="group" aria-label="Time range">
        <button type="button" class="range-btn active" data-range="15m">15m</button>
        <button type="button" class="range-btn" data-range="1h">1h</button>
        <button type="button" class="range-btn" data-range="6h">6h</button>
        <button type="button" class="range-btn" data-range="24h">24h</button>
      </div>
      <label class="refresh-group">Refresh interval
        <select class="refresh-select" id="refresh-interval">
          <option value="2000">2s</option>
          <option value="5000" selected>5s</option>
          <option value="10000">10s</option>
          <option value="30000">30s</option>
          <option value="60000">1m</option>
        </select>
      </label>
    </div>

    <div class="grid">
      <div class="card chart-card">
        <h2 class="title">CPU% and RAM MB over time</h2>
        <div class="chart-meta">
          <div class="chart-legend">
            <span class="legend-item"><span class="legend-swatch" style="background:#60a5fa"></span>CPU %</span>
            <span class="legend-item"><span class="legend-swatch" style="background:#34d399"></span>RAM MB</span>
          </div>
          <span id="cpu-ram-status">Awaiting telemetry…</span>
        </div>
        <svg id="chart-cpu-ram" class="chart" viewBox="0 0 900 220" preserveAspectRatio="none"></svg>
      </div>

      <div class="card chart-card">
        <h2 class="title">HTTP latency over time</h2>
        <div class="chart-meta">
          <div class="chart-legend">
            <span class="legend-item"><span class="legend-swatch" style="background:#f97316"></span>Display</span>
            <span class="legend-item"><span class="legend-swatch" style="background:#a78bfa"></span>Location</span>
            <span class="legend-item"><span class="legend-swatch" style="background:#f43f5e"></span>Spotify</span>
          </div>
          <span id="latency-status">Awaiting telemetry…</span>
        </div>
        <svg id="chart-latency" class="chart" viewBox="0 0 900 220" preserveAspectRatio="none"></svg>
      </div>

      <div class="card chart-card">
        <h2 class="title">Overlay chart: CPU% vs Display latency</h2>
        <div class="chart-meta">
          <div class="chart-legend">
            <span class="legend-item"><span class="legend-swatch" style="background:#38bdf8"></span>CPU %</span>
            <span class="legend-item"><span class="legend-swatch" style="background:#f59e0b"></span>Display latency (ms)</span>
          </div>
          <span id="overlay-status">Awaiting telemetry…</span>
        </div>
        <svg id="chart-overlay" class="chart" viewBox="0 0 900 220" preserveAspectRatio="none"></svg>
      </div>

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

      <div class="card" id="runtime-card">
        <h2 class="title">Server Runtime</h2>
        <div class="metric"><span>Process CPU</span><span class="value" id="runtime-cpu">0%</span></div>
        <div class="metric"><span>Working set</span><span class="value" id="runtime-working-set">0 MB</span></div>
        <div class="metric"><span>GC heap</span><span class="value" id="runtime-gc-heap">0 MB</span></div>
        <div class="metric"><span>Thread count</span><span class="value" id="runtime-threads">0</span></div>
        <div class="metric"><span>GC collections (0/1/2)</span><span class="value" id="runtime-gc-collections">0 / 0 / 0</span></div>
        <div class="history" id="runtime-history"></div>
      </div>

      <div class="card" id="correlation-card">
        <h2 class="title">CPU ↔ Display Latency Correlation</h2>
        <div class="metric"><span title="Pearson captures linear relationship from -1 to +1. Positive means higher CPU tends to coincide with higher display latency.">15m Pearson</span><span class="value" id="corr-15m-pearson">n/a</span></div>
        <div class="metric"><span title="Spearman captures monotonic rank relationship and is less sensitive to outliers.">15m Spearman</span><span class="value" id="corr-15m-spearman">n/a</span></div>
        <div class="metric"><span>15m Strength</span><span class="value" id="corr-15m-strength">n/a</span></div>
        <div class="metric"><span>15m Confidence</span><span class="value" id="corr-15m-confidence">insufficient</span></div>
        <div class="metric"><span title="Pearson captures linear relationship from -1 to +1. Positive means higher CPU tends to coincide with higher display latency.">1h Pearson</span><span class="value" id="corr-1h-pearson">n/a</span></div>
        <div class="metric"><span title="Spearman captures monotonic rank relationship and is less sensitive to outliers.">1h Spearman</span><span class="value" id="corr-1h-spearman">n/a</span></div>
        <div class="metric"><span>1h Strength</span><span class="value" id="corr-1h-strength">n/a</span></div>
        <div class="metric"><span>1h Confidence</span><span class="value" id="corr-1h-confidence">insufficient</span></div>
        <div class="hint" title="|r| under 0.3 is usually weak, 0.3-0.6 moderate, and 0.6+ strong. Correlation does not prove causation.">Interpretation: weak (&lt; 0.3), moderate (0.3–0.6), strong (≥ 0.6). Correlation does not imply causation.</div>
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
    const rangeMsByKey = {
      '15m': 15 * 60 * 1000,
      '1h': 60 * 60 * 1000,
      '6h': 6 * 60 * 60 * 1000,
      '24h': 24 * 60 * 60 * 1000
    };

    const chartSvgNs = 'http://www.w3.org/2000/svg';
    const chartConfig = { width: 900, height: 220, padding: { top: 16, right: 56, bottom: 24, left: 48 } };

    const dashboardState = {
      selectedRange: '15m',
      refreshIntervalMs: 5000,
      timerId: null,
      lastPayload: null
    };

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

    function formatMb(value) {
      return value === null || value === undefined ? 'n/a' : `${value} MB`;
    }

    function parseTimestamp(value) {
      return new Date(value).getTime();
    }

    function filterRange(points, rangeMs) {
      if (!points || points.length === 0) return [];
      const now = Date.now();
      const threshold = now - rangeMs;
      return points.filter(point => point.timestamp >= threshold);
    }

    function setRuntime(data) {
      const latest = data?.latest;
      document.getElementById('runtime-cpu').textContent = latest ? `${latest.processCpuPercent}%` : 'n/a';
      document.getElementById('runtime-working-set').textContent = latest ? formatMb(latest.workingSetMb) : 'n/a';
      document.getElementById('runtime-gc-heap').textContent = latest ? formatMb(latest.gcHeapMb) : 'n/a';
      document.getElementById('runtime-threads').textContent = latest ? latest.threadCount : 'n/a';
      document.getElementById('runtime-gc-collections').textContent = latest
        ? `${latest.gen0Collections} / ${latest.gen1Collections} / ${latest.gen2Collections}`
        : 'n/a';

      const history = document.getElementById('runtime-history');
      history.innerHTML = '';
      const rows = (data?.history ?? []).slice(-12).reverse();

      if (rows.length === 0) {
        history.textContent = 'No runtime samples yet.';
        return;
      }

      for (const entry of rows) {
        const row = document.createElement('div');
        row.className = 'history-row';
        const left = document.createElement('span');
        left.textContent = new Date(entry.timestampUtc).toLocaleTimeString();
        const right = document.createElement('span');
        right.textContent = `${entry.processCpuPercent}% · ${entry.workingSetMb} MB · GC ${entry.gen0Collections}/${entry.gen1Collections}/${entry.gen2Collections}`;
        row.appendChild(left);
        row.appendChild(right);
        history.appendChild(row);
      }
    }

    function classifyCorrelation(value) {
      if (value === null || value === undefined) return 'n/a';
      const magnitude = Math.abs(value);
      if (magnitude >= 0.6) return 'strong';
      if (magnitude >= 0.3) return 'moderate';
      return 'weak';
    }

    function formatCorrelation(value) {
      return value === null || value === undefined ? 'n/a' : value.toFixed(3);
    }

    function setCorrelation(prefix, data) {
      document.getElementById(`corr-${prefix}-pearson`).textContent = formatCorrelation(data?.pearson);
      document.getElementById(`corr-${prefix}-spearman`).textContent = formatCorrelation(data?.spearman);
      document.getElementById(`corr-${prefix}-strength`).textContent = classifyCorrelation(data?.pearson);
      document.getElementById(`corr-${prefix}-confidence`).textContent = data?.confidence ?? 'insufficient';
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

    function clearChart(svgId) {
      const svg = document.getElementById(svgId);
      svg.replaceChildren();
      return svg;
    }

    function createSvgElement(name, attrs) {
      const element = document.createElementNS(chartSvgNs, name);
      for (const [key, value] of Object.entries(attrs)) {
        element.setAttribute(key, value);
      }
      return element;
    }

    function computeDomain(values, forceZeroFloor) {
      if (values.length === 0) return null;
      let min = Math.min(...values);
      let max = Math.max(...values);
      if (forceZeroFloor) {
        min = Math.min(0, min);
      }
      if (max === min) {
        const wiggle = max === 0 ? 1 : Math.abs(max * 0.1);
        min -= wiggle;
        max += wiggle;
      }
      return { min, max };
    }

    function drawGrid(svg, xMin, xMax, yMin, yMax) {
      const { width, height, padding } = chartConfig;
      const innerWidth = width - padding.left - padding.right;
      const innerHeight = height - padding.top - padding.bottom;
      const yTicks = 4;
      const xTicks = 6;

      for (let i = 0; i <= yTicks; i++) {
        const y = padding.top + (innerHeight / yTicks) * i;
        svg.appendChild(createSvgElement('line', {
          x1: padding.left,
          y1: y.toFixed(2),
          x2: (padding.left + innerWidth).toFixed(2),
          y2: y.toFixed(2),
          stroke: 'rgba(148, 163, 184, 0.18)',
          'stroke-width': '1'
        }));
      }

      for (let i = 0; i <= xTicks; i++) {
        const x = padding.left + (innerWidth / xTicks) * i;
        svg.appendChild(createSvgElement('line', {
          x1: x.toFixed(2),
          y1: padding.top,
          x2: x.toFixed(2),
          y2: (padding.top + innerHeight).toFixed(2),
          stroke: 'rgba(148, 163, 184, 0.1)',
          'stroke-width': '1'
        }));
      }

      const axes = createSvgElement('rect', {
        x: padding.left,
        y: padding.top,
        width: innerWidth,
        height: innerHeight,
        fill: 'none',
        stroke: 'rgba(148, 163, 184, 0.25)',
        'stroke-width': '1'
      });
      svg.appendChild(axes);
    }

    function plotLine(svg, points, xDomain, yDomain, color) {
      const { width, height, padding } = chartConfig;
      const innerWidth = width - padding.left - padding.right;
      const innerHeight = height - padding.top - padding.bottom;
      const xSpan = xDomain.max - xDomain.min || 1;
      const ySpan = yDomain.max - yDomain.min || 1;

      const pathData = points.map((point, index) => {
        const xRatio = (point.timestamp - xDomain.min) / xSpan;
        const yRatio = (point.value - yDomain.min) / ySpan;
        const x = padding.left + (xRatio * innerWidth);
        const y = padding.top + ((1 - yRatio) * innerHeight);
        return `${index === 0 ? 'M' : 'L'} ${x.toFixed(2)} ${y.toFixed(2)}`;
      }).join(' ');

      svg.appendChild(createSvgElement('path', {
        d: pathData,
        fill: 'none',
        stroke: color,
        'stroke-width': '2.2',
        'stroke-linecap': 'round',
        'stroke-linejoin': 'round'
      }));
    }

    function addAxisLabels(svg, leftText, rightText) {
      const { width, padding } = chartConfig;
      svg.appendChild(createSvgElement('text', {
        x: 8,
        y: 18,
        fill: '#94a3b8',
        'font-size': '11'
      })).textContent = leftText;

      svg.appendChild(createSvgElement('text', {
        x: width - padding.right + 6,
        y: 18,
        fill: '#94a3b8',
        'font-size': '11'
      })).textContent = rightText;
    }

    function renderCpuRamChart(payload) {
      const rangeMs = rangeMsByKey[dashboardState.selectedRange];
      const points = (payload.runtime?.history ?? []).map(entry => ({
        timestamp: parseTimestamp(entry.timestampUtc),
        cpu: entry.processCpuPercent,
        ram: entry.workingSetMb
      }));

      const filtered = filterRange(points, rangeMs);
      const status = document.getElementById('cpu-ram-status');
      const svg = clearChart('chart-cpu-ram');
      if (filtered.length < 2) {
        status.textContent = 'Not enough runtime samples for selected range.';
        return;
      }

      const xDomain = { min: filtered[0].timestamp, max: filtered[filtered.length - 1].timestamp };
      const cpuDomain = computeDomain(filtered.map(point => point.cpu), true);
      const ramDomain = computeDomain(filtered.map(point => point.ram), false);

      drawGrid(svg, xDomain.min, xDomain.max, cpuDomain.min, cpuDomain.max);
      plotLine(svg, filtered.map(point => ({ timestamp: point.timestamp, value: point.cpu })), xDomain, cpuDomain, '#60a5fa');
      plotLine(svg, filtered.map(point => ({ timestamp: point.timestamp, value: point.ram })), xDomain, ramDomain, '#34d399');
      addAxisLabels(svg, `CPU (${cpuDomain.min.toFixed(1)}-${cpuDomain.max.toFixed(1)}%)`, `RAM (${ramDomain.min.toFixed(1)}-${ramDomain.max.toFixed(1)} MB)`);
      status.textContent = `${filtered.length} points in ${dashboardState.selectedRange}`;
    }

    function renderLatencyChart(payload) {
      const rangeMs = rangeMsByKey[dashboardState.selectedRange];
      const flatten = (rows, key) => (rows ?? []).map(item => ({ timestamp: parseTimestamp(item.timestampUtc), value: item.avgDurationMs, key }));
      const display = flatten(payload.timeSeries?.displayLatency, 'display');
      const location = flatten(payload.timeSeries?.locationLatency, 'location');
      const spotify = flatten(payload.timeSeries?.spotifyLatency, 'spotify');
      const all = [...display, ...location, ...spotify];
      const filteredAll = filterRange(all, rangeMs);
      const status = document.getElementById('latency-status');
      const svg = clearChart('chart-latency');

      if (filteredAll.length < 2) {
        status.textContent = 'Not enough latency samples for selected range.';
        return;
      }

      const grouped = {
        display: filteredAll.filter(point => point.key === 'display'),
        location: filteredAll.filter(point => point.key === 'location'),
        spotify: filteredAll.filter(point => point.key === 'spotify')
      };

      const xValues = filteredAll.map(point => point.timestamp);
      const yValues = filteredAll.map(point => point.value);
      const xDomain = { min: Math.min(...xValues), max: Math.max(...xValues) };
      const yDomain = computeDomain(yValues, true);

      drawGrid(svg, xDomain.min, xDomain.max, yDomain.min, yDomain.max);
      if (grouped.display.length > 1) plotLine(svg, grouped.display, xDomain, yDomain, '#f97316');
      if (grouped.location.length > 1) plotLine(svg, grouped.location, xDomain, yDomain, '#a78bfa');
      if (grouped.spotify.length > 1) plotLine(svg, grouped.spotify, xDomain, yDomain, '#f43f5e');
      addAxisLabels(svg, `Latency (${yDomain.min.toFixed(0)}-${yDomain.max.toFixed(0)} ms)`, '');

      status.textContent = `${filteredAll.length} samples in ${dashboardState.selectedRange}`;
    }

    function renderOverlayChart(payload) {
      const rangeMs = rangeMsByKey[dashboardState.selectedRange];
      const runtime = (payload.runtime?.history ?? []).map(entry => ({
        timestamp: parseTimestamp(entry.timestampUtc),
        cpu: entry.processCpuPercent
      }));
      const display = (payload.timeSeries?.displayLatency ?? []).map(entry => ({
        timestamp: parseTimestamp(entry.timestampUtc),
        latency: entry.avgDurationMs
      }));

      const filteredDisplay = filterRange(display, rangeMs);
      const filteredRuntime = filterRange(runtime, rangeMs);
      const status = document.getElementById('overlay-status');
      const svg = clearChart('chart-overlay');
      const bucketMs = (payload.timeSeries?.bucketSizeSeconds ?? 30) * 1000;

      const merged = [];
      for (const latencyPoint of filteredDisplay) {
        const cpuInBucket = filteredRuntime.filter(cpuPoint =>
          cpuPoint.timestamp >= latencyPoint.timestamp && cpuPoint.timestamp < latencyPoint.timestamp + bucketMs);

        if (cpuInBucket.length === 0) continue;
        merged.push({
          timestamp: latencyPoint.timestamp,
          cpu: cpuInBucket.reduce((sum, item) => sum + item.cpu, 0) / cpuInBucket.length,
          latency: latencyPoint.latency
        });
      }

      if (merged.length < 2) {
        status.textContent = 'Insufficient overlapping CPU/latency samples in selected range.';
        return;
      }

      const xDomain = { min: merged[0].timestamp, max: merged[merged.length - 1].timestamp };
      const cpuDomain = computeDomain(merged.map(point => point.cpu), true);
      const latencyDomain = computeDomain(merged.map(point => point.latency), true);

      drawGrid(svg, xDomain.min, xDomain.max, cpuDomain.min, cpuDomain.max);
      plotLine(svg, merged.map(point => ({ timestamp: point.timestamp, value: point.cpu })), xDomain, cpuDomain, '#38bdf8');
      plotLine(svg, merged.map(point => ({ timestamp: point.timestamp, value: point.latency })), xDomain, latencyDomain, '#f59e0b');
      addAxisLabels(svg, `CPU (${cpuDomain.min.toFixed(1)}-${cpuDomain.max.toFixed(1)}%)`, `Latency (${latencyDomain.min.toFixed(0)}-${latencyDomain.max.toFixed(0)} ms)`);

      status.textContent = `${merged.length} overlapping points in ${dashboardState.selectedRange}`;
    }

    function renderCharts(payload) {
      renderCpuRamChart(payload);
      renderLatencyChart(payload);
      renderOverlayChart(payload);
    }

    function updateRangeButtons() {
      for (const button of document.querySelectorAll('.range-btn')) {
        button.classList.toggle('active', button.dataset.range === dashboardState.selectedRange);
      }
    }

    function applyRange(rangeKey) {
      dashboardState.selectedRange = rangeKey;
      updateRangeButtons();
      if (dashboardState.lastPayload) {
        renderCharts(dashboardState.lastPayload);
      }
    }

    function restartRefreshTimer() {
      if (dashboardState.timerId !== null) {
        clearInterval(dashboardState.timerId);
      }

      dashboardState.timerId = setInterval(refresh, dashboardState.refreshIntervalMs);
    }

    async function refresh() {
      try {
        const response = await fetch('/api/telemetry/summary', { cache: 'no-store' });
        if (!response.ok) throw new Error(`HTTP ${response.status}`);
        const payload = await response.json();
        dashboardState.lastPayload = payload;

        setSection('display', payload.display);
        setSection('location', payload.location);
        setSection('spotify', payload.spotify);
        setRuntime(payload.runtime);
        setDevice(payload.device);
        setCorrelation('15m', payload.cpuToDisplayLatencyCorrelation15m);
        setCorrelation('1h', payload.cpuToDisplayLatencyCorrelation1h);
        renderCharts(payload);

        document.getElementById('updated').textContent = new Date(payload.generatedAtUtc).toLocaleTimeString();
      } catch (error) {
        document.getElementById('updated').textContent = `refresh failed (${error.message})`;
      }
    }

    for (const button of document.querySelectorAll('.range-btn')) {
      button.addEventListener('click', () => applyRange(button.dataset.range));
    }

    document.getElementById('refresh-interval').addEventListener('change', (event) => {
      dashboardState.refreshIntervalMs = Number(event.target.value);
      restartRefreshTimer();
      refresh();
    });

    updateRangeButtons();
    restartRefreshTimer();
    refresh();
  </script>
</body>
</html>
""";
}
