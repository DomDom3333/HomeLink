# HomeLink Device Firmware

## Table of Contents

1. [Overview](#overview)
2. [Target Hardware](#target-hardware)
3. [Dependencies & Libraries](#dependencies--libraries)
4. [How It Works](#how-it-works)
5. [Configuration Reference](#configuration-reference)
   - [WiFi Networks](#wifi-networks)
   - [Server URL](#server-url)
   - [Display Tuning](#display-tuning)
   - [Battery Monitoring](#battery-monitoring)
   - [Power & WiFi Performance](#power--wifi-performance)
   - [HTTP / Download Timeouts](#http--download-timeouts)
   - [Refresh Intervals](#refresh-intervals)
6. [Operational Flow (Boot Cycle)](#operational-flow-boot-cycle)
7. [WiFi Connection Strategy](#wifi-connection-strategy)
8. [Image Formats](#image-formats)
9. [ETag Caching](#etag-caching)
10. [Network Config Caching (Static IP)](#network-config-caching-static-ip)
11. [Display Rendering Pipeline](#display-rendering-pipeline)
12. [Battery Monitoring Detail](#battery-monitoring-detail)
13. [NVS / Flash Storage Keys](#nvs--flash-storage-keys)
14. [Serial Debug Output](#serial-debug-output)
15. [Troubleshooting](#troubleshooting)

---

## Overview

`Homelink.ino` is the firmware for the HomeLink e-paper display device. It runs on an ESP32-based board (LilyGo T5 or similar) with a 4.7" e-ink panel. On each wake cycle the device:

1. Reads the battery voltage (before WiFi starts).
2. Connects to the best available WiFi network using a multi-tier strategy.
3. Fetches a rendered display image from a HomeLink server using HTTP(S) with ETag-based conditional requests.
4. Converts and renders the image on the e-paper display.
5. Powers down WiFi and Bluetooth, then enters deep sleep until the next cycle.

Because the device spends almost all of its time in deep sleep, typical battery life is measured in days to weeks depending on update frequency, WiFi quality, and battery capacity.

---

## Target Hardware

| Component | Details |
|---|---|
| MCU | ESP32 or ESP32-S3 |
| Board | LilyGo T5 4.7" (recommended), or any ESP32 board with a compatible e-ink panel |
| Display | 960 × 540 pixel e-ink (EPD), 4-bit grayscale (16 shades) |
| Battery ADC | GPIO 14 (ESP32-S3) / GPIO 35 (legacy ESP32) |
| Battery divider | 2:1 on-board voltage divider (VBAT → ADC input) |
| Flash (NVS) | Used for WiFi / IP / ETag caching across full power cycles |
| RTC memory | Used for ETag and draw-count caching across deep sleep (no flash write) |

> **Note:** The ESP32-S3 ADC2 (GPIO14) cannot be read reliably while WiFi is active. The firmware reads the battery *before* starting WiFi to avoid this limitation.

---

## Dependencies & Libraries

| Library | Purpose |
|---|---|
| `WiFi.h` | WiFi STA management |
| `HTTPClient.h` | HTTP/HTTPS GET requests |
| `WiFiClientSecure.h` | TLS (HTTPS) support |
| `Preferences.h` | NVS (Non-Volatile Storage) key-value store |
| `epd_driver.h` | LilyGo / Epdiy e-paper display driver |
| `esp_sleep.h` | Deep sleep and wakeup configuration |
| `esp_bt.h` | Bluetooth disable (power saving) |
| `esp_heap_caps.h` | DMA-capable memory allocation |

Install the board support package for ESP32 via the Arduino Boards Manager, and the EPD driver from the [LilyGo-EPD47](https://github.com/Xinyuan-LilyGO/LilyGo-EPD47) repository.

---

## How It Works

```
┌─────────────────────────────────────────────────────────────┐
│                        BOOT / WAKE                          │
│  1. Set CPU to CPU_FREQ_MHZ                                 │
│  2. Read battery ADC (WiFi is OFF)                          │
│  3. Parse IMAGE_URL                                         │
│  4. Load ETag from RTC memory (or NVS on cold boot)         │
└────────────────────┬────────────────────────────────────────┘
                     │
                     ▼
┌─────────────────────────────────────────────────────────────┐
│                   WIFI CONNECT (smart)                      │
│  Try last-known AP → scan for strongest BSSID → fallback    │
│  Uses cached BSSID+channel for fast reconnect               │
│  Uses cached static IP to skip DHCP                         │
└────────────────────┬────────────────────────────────────────┘
                     │ fail → sleep SLEEP_FAIL_S
                     ▼
┌─────────────────────────────────────────────────────────────┐
│                   HTTP(S) FETCH                             │
│  GET IMAGE_URL?dither=false&deviceBattery=<pct>             │
│  If-None-Match: <cached ETag>                               │
│  → 304 Not Modified: skip display update                    │
│  → 200 OK: download binary image payload                    │
└────────────────────┬────────────────────────────────────────┘
                     │ fail → clear network/AP cache, sleep SLEEP_FAIL_S
                     ▼
┌─────────────────────────────────────────────────────────────┐
│                   DISPLAY RENDER                            │
│  WiFi + BT powered off before EPD update                    │
│  Detect format (2bpp / 4bpp / 8bpp)                         │
│  Full-frame DMA render (or tiled fallback)                  │
│  Periodic full clear based on FULL_CLEAR_EVERY_N_UPDATES    │
└────────────────────┬────────────────────────────────────────┘
                     │
                     ▼
┌─────────────────────────────────────────────────────────────┐
│                    DEEP SLEEP                               │
│  Changed: SLEEP_CHANGED_S (default 60 s)                    │
│  No change: SLEEP_SAME_S (default 300 s)                    │
└─────────────────────────────────────────────────────────────┘
```

---

## Configuration Reference

All user-facing settings live in the **USER SETTINGS** section at the top of `Homelink.ino`.

---

### WiFi Networks

```cpp
static const KnownAP KNOWN_APS[] = {
  {"AP1",           "password"},
  {"AP2",           "TopSecret!"},
  {"PhoneHotspot",  "hotspot-pass"},
};
```

Add as many networks as needed. The device will:
- Prefer the **last-known-working** AP.
- Fall back to a **scan** to find the strongest BSSID if the preferred AP is weak or unavailable.
- Try all configured APs in order as a final fallback.

There is no limit to the number of entries, but each adds a small amount of scan time on fallback.

---

### Server URL

```cpp
const char* IMAGE_URL = "https://MY.HOMELINK.DOMAIN/api/display/render?dither=false";
```

The URL of the HomeLink server's render endpoint. Supports both `http://` and `https://`. TLS certificate verification is **disabled** (`setInsecure()`) – use a firewall or private network if security is a concern.

The firmware automatically appends `deviceBattery=<percent>` as an additional query parameter on each request so the server can display the battery state.

---

### Display Tuning

| Macro | Default | Description |
|---|---|---|
| `INVERT_OUTPUT` | `0` | Set to `1` if the display colours are inverted (black appears white and vice-versa). |
| `LOW_NIBBLE_FIRST` | `1` | Swaps nibble packing order. Set to `1` to fix vertical "stripy gaps" artefacts. Set to `0` if your panel looks correct without it. |
| `TILE_H` | `40` | Tile height in rows used by the tiled rendering fallback (only active when a full-frame DMA allocation fails). Reduce if you see memory errors. |
| `FULL_CLEAR_EVERY_N_UPDATES` | `1` | How often a full EPD clear is performed. `1` = every update (cleanest image, most wear). `0` = never (except the very first draw after a power cycle). Higher values, e.g. `5`, trade some ghosting for reduced panel wear and faster updates. |
| `EPD_WIDTH` | `960` | Display width in pixels. Override if using a different panel. |
| `EPD_HEIGHT` | `540` | Display height in pixels. Override if using a different panel. |

---

### Battery Monitoring

| Macro | Default | Description |
|---|---|---|
| `BATTERY_ADC_PIN` | `14` (S3) / `35` (ESP32) | GPIO pin connected to the battery voltage divider output. |
| `BATTERY_ADC_DIVIDER` | `2.0` | Ratio of VBAT to ADC input voltage. The LilyGo T5 uses a 1:1 resistor divider, halving the battery voltage before it reaches the ADC. |
| `BATTERY_VOLTAGE_EMPTY` | `3.30 V` | Voltage reported as 0 % battery. |
| `BATTERY_VOLTAGE_FULL` | `4.20 V` | Voltage reported as 100 % battery. |
| `BATTERY_ADC_SAMPLES` | `8` | Number of ADC reads averaged per measurement. Higher values reduce noise at the cost of a few extra milliseconds. |

The measured battery percentage is sent to the server as the `deviceBattery` query parameter and can be rendered on the display by the HomeLink server.

---

### Power & WiFi Performance

| Macro | Default | Description |
|---|---|---|
| `CPU_FREQ_MHZ` | `80` | CPU clock speed in MHz during the active window. `80` gives the best power efficiency. `160` is faster but draws more current. `240` is available but rarely necessary. |
| `WIFI_TX_POWER` | `WIFI_POWER_19_5dBm` | WiFi transmit power. Reduce (e.g. `WIFI_POWER_8_5dBm`) if the AP is close – this saves measurable current. Increase if you get flaky connections. |
| `WIFI_GOOD_RSSI_DBM` | `-67` | Signal quality threshold (dBm). If the last-known AP connects above this value the device skips scanning, saving time and energy. |
| `WIFI_SCAN_ATTEMPTS` | `3` | Maximum number of scan rounds before giving up and connecting to the best seen candidate anyway. |
| `WIFI_SCAN_RETRY_DELAY_MS` | `250` | Base delay (ms) between scan attempts. Scales with attempt number (attempt × delay). |
| `ETAG_PERSIST_EVERY_N_CHANGES` | `0` | How often the ETag is written to flash. `0` = RTC memory only (no flash writes during operation, ETag lost on full power cycle). `1` = every change. Flash writes extend cold-boot re-fetches but protect against full power loss. |

---

### HTTP / Download Timeouts

| Macro | Default | Description |
|---|---|---|
| `WIFI_FAST_CONNECT_MS` | `5000` | Maximum time (ms) for a fast reconnect using a cached BSSID+channel. |
| `WIFI_CONNECT_MS` | `30000` | Maximum time (ms) for a normal (non-cached) WiFi connection attempt. |
| `WIFI_RETRY_DHCP_MS` | `20000` | Maximum time (ms) for a DHCP retry after a static-IP failure. |
| `HTTP_GET_TIMEOUT_MS` | `30000` | Maximum time (ms) for the initial HTTP GET (DNS + TLS handshake + headers). |
| `STREAM_READ_BLOCK_TIMEOUT_MS` | `2000` | Per-read block timeout (ms) on the TCP stream. |
| `DOWNLOAD_STALL_TIMEOUT_MS` | `60000` | If no bytes are received for this long, the download is aborted. |
| `DOWNLOAD_OVERALL_TIMEOUT_MS` | `120000` | Hard cap on total download duration (ms). |
| `DOWNLOAD_CHUNK_BYTES` | `16384` | Maximum bytes read per stream-read call. |

---

### Refresh Intervals

Configured as constants near the top of the file:

| Constant | Default | Description |
|---|---|---|
| `SLEEP_CHANGED_S` | `60` (1 min) | Deep-sleep duration after a successful display update where the content changed. |
| `SLEEP_SAME_S` | `300` (5 min) | Deep-sleep duration after a successful request where the server returned 304 Not Modified (content unchanged). |
| `SLEEP_FAIL_S` | `60` (1 min) | Deep-sleep duration after any failure (WiFi, HTTP, or render error). |

On failure, the firmware also clears the cached static IP and BSSID for the active AP, forcing a fresh DHCP and scan-based connect on the next wake.

---

## Operational Flow (Boot Cycle)

### 1 — CPU frequency

`setCpuFrequencyMhz(CPU_FREQ_MHZ)` is called immediately to reduce active current before anything else runs.

### 2 — Battery read

`batteryInit()` configures the ADC pin, then `readBatteryPercentOnce()` takes `BATTERY_ADC_SAMPLES` averaged readings while WiFi is off. The result is stored in `gDeviceBattery` and later appended to the request URL.

### 3 — URL parse

`IMAGE_URL` is split into scheme, host, port, and URI components. If parsing fails the device sleeps for `SLEEP_FAIL_S`.

### 4 — ETag load

On wakeup from deep sleep, the ETag is loaded from **RTC memory** (fast, no flash). On a cold boot (power cycle), the ETag is loaded from **NVS flash** as a fallback.

### 5 — WiFi connect

See [WiFi Connection Strategy](#wifi-connection-strategy).

### 6 — HTTP fetch

A conditional GET is issued with:
- `If-None-Match: <ETag>` (if a cached ETag exists)
- `Accept: application/octet-stream`
- `deviceBattery=<percent>` appended to the URI

A **304 Not Modified** response skips the display update and the device goes back to sleep after `SLEEP_SAME_S`.  
A **200 OK** response triggers a full download and display render.

### 7 — Display render

WiFi and Bluetooth are powered off before driving the EPD to reduce peak current and avoid resets. See [Display Rendering Pipeline](#display-rendering-pipeline).

### 8 — Deep sleep

The device powers down all radios and enters deep sleep. RTC memory (ETag, draw count) is retained.

---

## WiFi Connection Strategy

The firmware uses a multi-tier connection strategy to balance speed and reliability:

```
Priority 1: Last-known-working AP + cached BSSID+channel  (fastest, ~1-2 s)
     ↓ fails or RSSI < WIFI_GOOD_RSSI_DBM
Priority 2: Scan → strongest known BSSID (best for mesh systems)
     ↓ all scans below threshold → connect to best seen anyway
Priority 3: Try all configured APs in order (handles hidden SSIDs)
```

**Per-AP state stored in NVS:**
- Last-known-working AP index
- BSSID + channel for fast reconnect (avoids scan)
- Cached DHCP lease (IP, gateway, subnet, DNS) to use as static IP and avoid DHCP delay

On failure, the caches for the active AP are invalidated so the next wake starts fresh.

---

## Image Formats

The firmware auto-detects the payload format based on the response `Content-Length`:

| Format | Payload size | Description |
|---|---|---|
| `IN_2BPP_PACKED` | `WIDTH × HEIGHT / 4` bytes | 2 bits per pixel packed (4 pixels/byte), MSB first. Values 0–3 mapped to 16 shades. |
| `IN_4BPP_PACKED` | `WIDTH × HEIGHT / 2` bytes | 4 bits per pixel packed (2 pixels/byte). Values 0–15 = direct shade index. |
| `IN_8BPP_RAW` | `WIDTH × HEIGHT` bytes | 1 byte per pixel. Full 0–255 range mapped to 16 shades; or detected as 2bpp/4bpp if the maximum observed value is ≤ 3 or ≤ 15 respectively. |

All formats are converted internally to **4bpp packed** (the EPD driver's native format) with optional inversion and nibble-swap applied.

The server's `/api/display/render` endpoint controls the actual pixel format; the firmware accepts all three transparently.

---

## ETag Caching

ETag caching is a key power-saving feature. The server returns an ETag header with every image response. On subsequent requests, the device sends `If-None-Match: <etag>`, and the server replies with HTTP **304 Not Modified** if nothing has changed — the device then skips the download and EPD update entirely.

```
Storage tier    Survives deep sleep?   Survives full power loss?
RTC memory      ✅ Yes                 ❌ No
NVS flash       ✅ Yes                 ✅ Yes
```

The firmware prioritises **RTC memory** to avoid flash wear. It only writes to NVS flash when `ETAG_PERSIST_EVERY_N_CHANGES` conditions are met (default: never, RTC only).

---

## Network Config Caching (Static IP)

After the first successful DHCP lease for each AP, the firmware saves the IP/gateway/subnet/DNS to NVS flash under per-AP keys. On subsequent boots for the same AP, it configures a static IP immediately, skipping the DHCP round-trip and saving ~500ms–2s per connection.

If a static IP results in a connection failure, the firmware retries with DHCP and updates the stored config.

Per-AP NVS keys (where `N` is the AP index 0-based):

| Key | Type | Content |
|---|---|---|
| `nNok` | UChar | `1` = static config valid |
| `nNip` | UInt | IP address as uint32 |
| `nNgw` | UInt | Gateway as uint32 |
| `nNsn` | UInt | Subnet mask as uint32 |
| `nNd1` | UInt | Primary DNS as uint32 |
| `nNd2` | UInt | Secondary DNS as uint32 |
| `aNok` | UChar | `1` = BSSID/channel cache valid |
| `aNch` | UChar | WiFi channel number |
| `aNbs` | Bytes | BSSID (6 bytes) |

---

## Display Rendering Pipeline

```
Raw HTTP payload
      │
      ▼
 detectFormat()  ──→  IN_2BPP_PACKED / IN_4BPP_PACKED / IN_8BPP_RAW
      │
      ▼
 powerDownRadios()  (WiFi + BT off)
      │
      ▼
 epd_init() + epd_poweron()
      │
      ├─ if shouldFullClearNow() → epd_clear()
      │
      ▼
 allocDMABuffer(WIDTH × HEIGHT / 2)
      │
      ├─ SUCCESS → convert full frame → epd_draw_grayscale_image(full)
      │
      └─ FAIL    → tiled loop (TILE_H rows at a time) → epd_draw_grayscale_image(tile)
      │
      ▼
 epd_poweroff()
```

**Conversion details:**
- `IN_8BPP_RAW` → 4bpp: samples the buffer to detect effective bit depth (2 or 4), then scales accordingly.
- `IN_4BPP_PACKED` → 4bpp: re-packs nibbles with invert and nibble-swap applied.
- `IN_2BPP_PACKED` → 4bpp: expands each 2-bit value to 4-bit (`value × 5`), then packs pairs.

`INVERT_OUTPUT` and `LOW_NIBBLE_FIRST` are applied at the nibble level in the `invert4()` and `PACK_NIBBLES()` helpers, affecting all formats uniformly.

---

## Battery Monitoring Detail

```
VBAT ──[R1]──┬──[R2]── GND
              │
           BATTERY_ADC_PIN (ADC input, 0 – 3.3 V)
```

```
V_adc  = average of BATTERY_ADC_SAMPLES analogReadMilliVolts() calls
V_bat  = V_adc × BATTERY_ADC_DIVIDER
percent = (V_bat - BATTERY_VOLTAGE_EMPTY) / (BATTERY_VOLTAGE_FULL - BATTERY_VOLTAGE_EMPTY) × 100
```

The result is clamped to `[0, 100]` and transmitted to the server as `deviceBattery=<N>`. The server can use this value to overlay a battery indicator on the rendered image.

A result of `-1` means the ADC read returned zero (likely a hardware fault or unsupported board) and the parameter is omitted from the URL.

---

## NVS / Flash Storage Keys

All keys live in the NVS namespace `"homelink"`.

| Key | Type | Description |
|---|---|---|
| `etag` | String | Persisted ETag (written only when `ETAG_PERSIST_EVERY_N_CHANGES` conditions met) |
| `lastap` | Int | Index into `KNOWN_APS[]` of the last successfully connected AP |
| `nNok` | UChar | Static IP cache valid flag for AP index N |
| `nNip` | UInt | Cached IP for AP N |
| `nNgw` | UInt | Cached gateway for AP N |
| `nNsn` | UInt | Cached subnet for AP N |
| `nNd1` | UInt | Cached primary DNS for AP N |
| `nNd2` | UInt | Cached secondary DNS for AP N |
| `aNok` | UChar | BSSID/channel cache valid flag for AP index N |
| `aNch` | UChar | Cached channel for AP N |
| `aNbs` | Bytes (6) | Cached BSSID for AP N |

---

## Serial Debug Output

Connect at **115200 baud**. The firmware logs every major step:

```
Reset reason: 5
Wakeup cause: 4
CPU MHz: 80
Battery: 78%
Loaded RTC ETag: "abc123def456"
Last-known-working AP idx: 1
Connecting to AP idx 1 (AP2)
Fast connect using cached BSSID+channel (ch 6, AA:BB:CC:DD:EE:FF) ...
Fast connect OK.
WiFi OK, IP: 192.168.1.42
Connected details: RSSI -58 dBm, ch 6, BSSID AA:BB:CC:DD:EE:FF
Post-connect RSSI: -58 dBm
RSSI is good enough; skipping scan.
Refreshing image...
Refresh: GET /api/display/render?dither=false&deviceBattery=78
If-None-Match: "abc123def456"
HTTP GET done in 412 ms, code=304
HTTP 304 Not Modified
No change; skipping display update.
Deep sleep for 300s
```

When content changes:

```
HTTP GET done in 823 ms, code=200
Content-Length: 259200
RSSI: -58 dBm, free heap: 148320
DL 16384/259200 bytes (elapsed 312 ms)
...
DL 259200/259200 bytes (elapsed 2914 ms)
Payload format: 2
WiFi: off before display
EPD: full clear
Rendering full-frame (single call).
Display: done in 3241 ms
Deep sleep for 60s
```

---

## Troubleshooting

| Symptom | Likely cause | Fix |
|---|---|---|
| Display is inverted (black/white swapped) | Wrong polarity for this panel | Set `INVERT_OUTPUT 1` |
| Vertical stripy gaps / column artefacts | Wrong nibble packing order | Set `LOW_NIBBLE_FIRST 1` (default) or `0` to toggle |
| WiFi keeps failing | Weak signal, wrong credentials, or stale BSSID cache | Check `KNOWN_APS`, reduce `WIFI_TX_POWER` floor, check AP is reachable |
| "ADC2 / WiFi conflict" watchdog reset | Battery read after WiFi started (ESP32-S3) | Firmware reads battery before WiFi — check `BATTERY_ADC_PIN` is correct |
| "Allocation failed for download buffer" | Insufficient heap / PSRAM not enabled | Enable PSRAM in Arduino board settings; or reduce image resolution on server |
| "Download incomplete" | Slow/flaky network | Increase `DOWNLOAD_STALL_TIMEOUT_MS` or move AP closer |
| "Unsupported payload size" | Server returned unexpected byte count | Verify server is configured for 960×540 and a known format |
| Image ghosting / afterimages | Too infrequent full clears | Reduce `FULL_CLEAR_EVERY_N_UPDATES` (lower = more clears) |
| Battery drains quickly | Update interval too short, CPU too fast, AP too far | Increase `SLEEP_CHANGED_S` / `SLEEP_SAME_S`; lower `CPU_FREQ_MHZ`; reduce `WIFI_TX_POWER` |
| TLS handshake timeout | Server slow or DNS resolution slow | Increase `HTTP_GET_TIMEOUT_MS` |
| "No change" every cycle despite updates | ETag mismatch between server and device | Clear NVS with `gPrefs.clear()` once, or set `ETAG_PERSIST_EVERY_N_CHANGES 1` |

