#include <WiFi.h>
#include <HTTPClient.h>
#include <WiFiClientSecure.h>
#include <Preferences.h>
#include "epd_driver.h"

#include "esp_sleep.h"
#include "esp_attr.h"

#if defined(ESP32)
  #include "esp_system.h" // esp_reset_reason
#endif

#if defined(ESP32)
  #include "esp_heap_caps.h"
  #include "esp_bt.h"
#endif

// ---------------- USER SETTINGS ----------------

// Configure multiple known WiFi networks here (SSID + password).
// The device will prefer the last-known-working network, and will only do a scan when that one fails.
struct KnownAP {
  const char* ssid;
  const char* pass;
};

static const KnownAP KNOWN_APS[] = {
  {"AP1", "password"},
  {"AP2", "TopSecret!"},
  {"PhoneHotspot", "hotspot-pass"},
};
static const uint8_t KNOWN_APS_COUNT = sizeof(KNOWN_APS) / sizeof(KNOWN_APS[0]);

const char* IMAGE_URL = "https://MY.HOMELINK.DOMAIN/api/display/render?dither=false";
const char* USER_AGENT =
  "Mozilla/5.0 (ESP32; LilyGoT5) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

// If output looks inverted, set to 1
#define INVERT_OUTPUT 0

// IMPORTANT FIX: swap nibble order (this usually fixes the vertical “stripy gaps”)
#define LOW_NIBBLE_FIRST 1

// Tile height used only as fallback when full-frame DMA alloc fails
#define TILE_H 40

// E-paper clearing policy:
// Full clears are expensive (time + energy) and increase wear.
// We clear only periodically to limit ghosting.
// Set to 1 to clear every update; 0 to never clear (except first draw after power-cycle).
#define FULL_CLEAR_EVERY_N_UPDATES 1

// Persist ETag to flash only occasionally (RTC retains it across deep sleep).
// Set to 1 to persist every time it changes; 0 to never persist (RTC only).
#define ETAG_PERSIST_EVERY_N_CHANGES 0

// Lower CPU frequency reduces active current; TLS may take slightly longer.
// 80 is a good power-first value. If you want faster updates, try 160.
#define CPU_FREQ_MHZ 80

// ---------------- Battery monitoring ----------------
// Sends the battery state to the render endpoint as query parameter `deviceBattery`.
//
// IMPORTANT (ESP32-S3 / T5-4.7-S3):
// The board's "Battery ADC" is on GPIO14 (ADC2). ADC2 reads are not reliable while WiFi is running.
// To avoid hangs / watchdog resets, this sketch reads the battery *before* starting WiFi.

#ifndef BATTERY_ADC_PIN
  #if defined(CONFIG_IDF_TARGET_ESP32S3) || defined(ARDUINO_ESP32S3_DEV)
    #define BATTERY_ADC_PIN 14
  #else
    // Legacy ESP32 variants commonly route VBAT to GPIO35.
    #define BATTERY_ADC_PIN 35
  #endif
#endif

// Divider ratio between battery and ADC input (VBAT is typically halved by an onboard divider on T5 boards).
#ifndef BATTERY_ADC_DIVIDER
  #define BATTERY_ADC_DIVIDER 2.0f
#endif

// Voltage range used for percent conversion (adjust to taste).
#ifndef BATTERY_VOLTAGE_EMPTY
  #define BATTERY_VOLTAGE_EMPTY 3.30f
#endif
#ifndef BATTERY_VOLTAGE_FULL
  #define BATTERY_VOLTAGE_FULL 4.20f
#endif

// Number of ADC samples averaged per reading.
#ifndef BATTERY_ADC_SAMPLES
  #define BATTERY_ADC_SAMPLES 8
#endif

// Battery percent for this wake cycle (read once before WiFi starts).
static int gDeviceBattery = -1;


// Reduce WiFi transmit power (big win if AP is close). If you get flaky WiFi, raise this.
#ifndef WIFI_TX_POWER
  #define WIFI_TX_POWER WIFI_POWER_19_5dBm
#endif

// Connection timeouts
#define WIFI_FAST_CONNECT_MS 5000   // attempt cached BSSID+channel connect this long
#define WIFI_CONNECT_MS      30000  // normal connect this long
#define WIFI_RETRY_DHCP_MS   20000  // DHCP retry after static failure


// RSSI-based selection policy
// - If the last-known-working AP connects with >= WIFI_GOOD_RSSI_DBM, we skip scanning.
// - Otherwise we do a few scans and connect to the strongest BSSID we see (important on mesh systems).
#define WIFI_GOOD_RSSI_DBM        (-67)
#define WIFI_SCAN_ATTEMPTS        3
#define WIFI_SCAN_RETRY_DELAY_MS  250

// HTTP / TLS timeouts
// - GET() can block on DNS/TLS/headers. Bound it.
// - Use a short per-read timeout and a separate stall timeout.
#define HTTP_GET_TIMEOUT_MS             30000
#define STREAM_READ_BLOCK_TIMEOUT_MS     2000
#define DOWNLOAD_STALL_TIMEOUT_MS       60000
#define DOWNLOAD_OVERALL_TIMEOUT_MS    120000
#define DOWNLOAD_CHUNK_BYTES            16384

// Counts successful display updates across deep sleep (RTC memory, no flash writes).
RTC_DATA_ATTR uint32_t gDrawCount = 0;

// RTC ETag cache (avoids flash writes; survives deep sleep).
RTC_DATA_ATTR bool     gRtcEtagValid = false;
RTC_DATA_ATTR char     gRtcEtag[128] = {0};
RTC_DATA_ATTR uint32_t gRtcEtagChangeCount = 0;

#ifndef EPD_WIDTH
  #define EPD_WIDTH  960
#endif
#ifndef EPD_HEIGHT
  #define EPD_HEIGHT 540
#endif

static const uint16_t WIDTH  = (uint16_t)EPD_WIDTH;
static const uint16_t HEIGHT = (uint16_t)EPD_HEIGHT;
static const size_t   PIXELS = (size_t)WIDTH * (size_t)HEIGHT;

// Refresh intervals (deep sleep)
static const uint32_t SLEEP_CHANGED_S = 60UL;        // 1 minute after change
static const uint32_t SLEEP_SAME_S    = 5UL * 60UL;  // 5 minutes after no change
static const uint32_t SLEEP_FAIL_S    = 60UL;        // 1 minute on failure

// ---- Preferences (persist across boots) ----
static Preferences gPrefs;
static const char* NVS_NS = "homelink";

// ETag (flash) fallback, used only if RTC ETag is not valid
static String      gCachedEtag;
static const char* NVS_ETAG = "etag";

// Last-known-working AP index (0..KNOWN_APS_COUNT-1), or -1
static const char* NVS_LAST_AP = "lastap";

// Parsed URL parts
static bool     gUrlHttps = true;
static String   gUrlHost;
static uint16_t gUrlPort = 443;
static String   gUrlUri;

// Active AP index for this boot (for targeted cache clearing)
static int8_t gActiveApIndex = -1;

static uint8_t* gInBuf = nullptr;
static size_t   gInLen = 0;

static WiFiClientSecure gTlsClient;   // reused, but each request does http.end()

struct ScanCandidate {
  int8_t  apIdx;     // index into KNOWN_APS
  int32_t rssi;      // dBm
  uint8_t bssid[6];  // strongest BSSID for that SSID
  uint8_t channel;
  bool    valid;
};

typedef enum {
  IN_UNKNOWN = 0,
  IN_2BPP_PACKED,   // W*H/4
  IN_4BPP_PACKED,   // W*H/2
  IN_8BPP_RAW       // W*H
} InFormat;

#if LOW_NIBBLE_FIRST
  // byte = (right<<4) | left
  #define PACK_NIBBLES(left4, right4) ( (uint8_t)(((right4 & 0x0F) << 4) | (left4 & 0x0F)) )
#else
  // byte = (left<<4) | right
  #define PACK_NIBBLES(left4, right4) ( (uint8_t)(((left4 & 0x0F) << 4) | (right4 & 0x0F)) )
#endif

static inline uint8_t invert4(uint8_t v4) {
#if INVERT_OUTPUT
  return (uint8_t)(15 - (v4 & 0x0F));
#else
  return (uint8_t)(v4 & 0x0F);
#endif
}

// ---------------- Utilities ----------------
static uint32_t ipToU32(const IPAddress& ip) {
  return ((uint32_t)ip[0] << 24) | ((uint32_t)ip[1] << 16) | ((uint32_t)ip[2] << 8) | (uint32_t)ip[3];
}
static IPAddress u32ToIp(uint32_t v) {
  return IPAddress((uint8_t)(v >> 24), (uint8_t)(v >> 16), (uint8_t)(v >> 8), (uint8_t)v);
}

static bool parseUrl(const char* url, bool* outHttps, String* outHost, uint16_t* outPort, String* outUri) {
  String s(url);

  bool https = s.startsWith("https://");
  bool http  = s.startsWith("http://");
  if (!https && !http) return false;

  int schemeLen = https ? 8 : 7;
  int slash = s.indexOf('/', schemeLen);
  String hostPort = (slash >= 0) ? s.substring(schemeLen, slash) : s.substring(schemeLen);
  String uri = (slash >= 0) ? s.substring(slash) : String("/");

  String host;
  uint16_t port = https ? 443 : 80;

  int colon = hostPort.indexOf(':');
  if (colon >= 0) {
    host = hostPort.substring(0, colon);
    String p = hostPort.substring(colon + 1);
    int pi = p.toInt();
    if (pi > 0 && pi < 65536) port = (uint16_t)pi;
  } else {
    host = hostPort;
  }

  if (!host.length()) return false;

  *outHttps = https;
  *outHost = host;
  *outPort = port;
  *outUri = uri;
  return true;
}

static void printBootInfo() {
#if defined(ESP32)
  esp_reset_reason_t rr = esp_reset_reason();
  esp_sleep_wakeup_cause_t wc = esp_sleep_get_wakeup_cause();
  Serial.print("Reset reason: ");
  Serial.println((int)rr);
  Serial.print("Wakeup cause: ");
  Serial.println((int)wc);
  Serial.print("CPU MHz: ");
  Serial.println(getCpuFrequencyMhz());
#endif
}

static bool waitForWiFi(uint32_t timeoutMs) {
  uint32_t start = millis();
  while (millis() - start < timeoutMs) {
    if (WiFi.status() == WL_CONNECTED) return true;
    delay(50);
  }
  return (WiFi.status() == WL_CONNECTED);
}


// ---------------- Battery monitoring helpers ----------------
static void batteryInit() {
  pinMode(BATTERY_ADC_PIN, INPUT);

#if defined(ARDUINO_ARCH_ESP32)
  // 12-bit ADC, widen input range for the divider output.
  analogReadResolution(12);
  analogSetPinAttenuation(BATTERY_ADC_PIN, ADC_11db);
#endif
}

static int readBatteryPercentOnce() {
  // Read ADC while WiFi is OFF (important for ESP32-S3 ADC2 pins like GPIO14).
#if defined(ESP32)
  WiFi.mode(WIFI_OFF);
#endif

  uint32_t sumMv = 0;

  for (int i = 0; i < (int)BATTERY_ADC_SAMPLES; i++) {
#if defined(ARDUINO_ARCH_ESP32)
    sumMv += (uint32_t)analogReadMilliVolts(BATTERY_ADC_PIN);
#else
    sumMv += (uint32_t)analogRead(BATTERY_ADC_PIN);
#endif
    delay(2); // yield
  }

  uint32_t mvAdc = (BATTERY_ADC_SAMPLES <= 1) ? sumMv : (sumMv / (uint32_t)BATTERY_ADC_SAMPLES);

#if !defined(ARDUINO_ARCH_ESP32)
  // Best-effort conversion for non-ESP32 cores (assumes 12-bit ADC and 3.3V reference).
  mvAdc = (uint32_t)((mvAdc * 3300UL) / 4095UL);
#endif

  if (mvAdc == 0) return -1;

  const float vAdc = (float)mvAdc / 1000.0f;
  const float vBat = vAdc * (float)BATTERY_ADC_DIVIDER;

  float pct = (vBat - (float)BATTERY_VOLTAGE_EMPTY) /
              ((float)BATTERY_VOLTAGE_FULL - (float)BATTERY_VOLTAGE_EMPTY) * 100.0f;

  if (pct < 0.0f) pct = 0.0f;
  if (pct > 100.0f) pct = 100.0f;

  return (int)(pct + 0.5f);
}

static String appendDeviceBatteryToUri(const String& uri, int batteryPercent) {
  if (batteryPercent < 0) return uri;

  String out = uri;
  if (out.indexOf('?') >= 0) out += '&';
  else out += '?';

  out += "deviceBattery=";
  out += String(batteryPercent);
  return out;
}


// ---------------- Memory helpers ----------------
static void* allocGeneral(size_t n) {
  void* p = nullptr;
#if defined(BOARD_HAS_PSRAM)
  p = ps_malloc(n);
#endif
  if (!p) p = malloc(n);
  return p;
}

static void* allocDMABuffer(size_t n) {
#if defined(ESP32)
  void* p = heap_caps_malloc(n, MALLOC_CAP_DMA | MALLOC_CAP_8BIT);
  if (p) return p;
#endif
  return malloc(n);
}

static void freeBuffer(void* p) {
  if (p) free(p);
}

// ---------------- RTC ETag helpers ----------------
static void rtcSetEtag(const String& etag) {
  size_t n = (etag.length() < (sizeof(gRtcEtag) - 1)) ? etag.length() : (sizeof(gRtcEtag) - 1);
  memcpy(gRtcEtag, etag.c_str(), n);
  gRtcEtag[n] = 0;
  gRtcEtagValid = (n > 0);
}

static void maybePersistEtagToFlash() {
#if ETAG_PERSIST_EVERY_N_CHANGES == 0
  return;
#else
  if (gRtcEtagChangeCount == 0) return;
  if ((gRtcEtagChangeCount % (uint32_t)ETAG_PERSIST_EVERY_N_CHANGES) == 0) {
    gPrefs.putString(NVS_ETAG, gCachedEtag);
    Serial.println("Persisted ETag to flash (rate-limited).");
  }
#endif
}

// ---------------- Power helpers ----------------
static void powerDownRadios() {
  WiFi.disconnect(true, true);
  WiFi.mode(WIFI_OFF);
  delay(50);

#if defined(ESP32)
  btStop();
  esp_bt_controller_disable();
#endif
}

static void deepSleepSeconds(uint32_t seconds) {
  Serial.print("Deep sleep for ");
  Serial.print(seconds);
  Serial.println("s");

  powerDownRadios();

#if defined(ESP32)
  // Save a bit more sleep current. Keep RTC_SLOW_MEM on (we store RTC_DATA_ATTR there).
  esp_sleep_pd_config(ESP_PD_DOMAIN_RTC_PERIPH, ESP_PD_OPTION_OFF);
  esp_sleep_pd_config(ESP_PD_DOMAIN_RTC_FAST_MEM, ESP_PD_OPTION_OFF);
#endif

  esp_sleep_enable_timer_wakeup((uint64_t)seconds * 1000000ULL);
  Serial.flush();
  esp_deep_sleep_start();
}

// ---------------- Per-AP network cache helpers (static IP after first DHCP per AP) ----------------
static void makeKey(char out[16], const char* fmt, uint8_t idx) {
  // Preferences key max length is small; keep keys short.
  // fmt examples: "n%uok", "n%uip", "a%ubs"
  snprintf(out, 16, fmt, (unsigned)idx);
  out[15] = 0;
}

static bool loadNetworkConfigForAp(uint8_t idx, IPAddress* ip, IPAddress* gw, IPAddress* sn, IPAddress* dns1, IPAddress* dns2) {
  char k_ok[16], k_ip[16], k_gw[16], k_sn[16], k_d1[16], k_d2[16];
  makeKey(k_ok, "n%uok", idx);
  makeKey(k_ip, "n%uip", idx);
  makeKey(k_gw, "n%ugw", idx);
  makeKey(k_sn, "n%usn", idx);
  makeKey(k_d1, "n%ud1", idx);
  makeKey(k_d2, "n%ud2", idx);

  uint8_t ok = gPrefs.getUChar(k_ok, 0);
  if (!ok) return false;

  uint32_t ip_u   = gPrefs.getUInt(k_ip, 0);
  uint32_t gw_u   = gPrefs.getUInt(k_gw, 0);
  uint32_t sn_u   = gPrefs.getUInt(k_sn, 0);
  uint32_t dns1_u = gPrefs.getUInt(k_d1, 0);
  uint32_t dns2_u = gPrefs.getUInt(k_d2, 0);

  if (!ip_u || !gw_u || !sn_u) return false;

  *ip   = u32ToIp(ip_u);
  *gw   = u32ToIp(gw_u);
  *sn   = u32ToIp(sn_u);
  *dns1 = dns1_u ? u32ToIp(dns1_u) : IPAddress(0,0,0,0);
  *dns2 = dns2_u ? u32ToIp(dns2_u) : IPAddress(0,0,0,0);
  return true;
}

static void saveNetworkConfigFromDhcpForAp(uint8_t idx) {
  IPAddress ip   = WiFi.localIP();
  IPAddress gw   = WiFi.gatewayIP();
  IPAddress sn   = WiFi.subnetMask();
  IPAddress dns1 = WiFi.dnsIP(0);
  IPAddress dns2 = WiFi.dnsIP(1);

  if ((uint32_t)ip == 0 || (uint32_t)gw == 0 || (uint32_t)sn == 0) {
    Serial.println("DHCP values invalid; not caching network config.");
    return;
  }

  char k_ok[16], k_ip[16], k_gw[16], k_sn[16], k_d1[16], k_d2[16];
  makeKey(k_ok, "n%uok", idx);
  makeKey(k_ip, "n%uip", idx);
  makeKey(k_gw, "n%ugw", idx);
  makeKey(k_sn, "n%usn", idx);
  makeKey(k_d1, "n%ud1", idx);
  makeKey(k_d2, "n%ud2", idx);

  gPrefs.putUChar(k_ok, 1);
  gPrefs.putUInt(k_ip, ipToU32(ip));
  gPrefs.putUInt(k_gw, ipToU32(gw));
  gPrefs.putUInt(k_sn, ipToU32(sn));
  gPrefs.putUInt(k_d1, ipToU32(dns1));
  gPrefs.putUInt(k_d2, ipToU32(dns2));

  Serial.println("Cached DHCP network config for future static use (per-AP).");
}

static void clearNetworkCacheForAp(uint8_t idx) {
  char k_ok[16], k_ip[16], k_gw[16], k_sn[16], k_d1[16], k_d2[16];
  makeKey(k_ok, "n%uok", idx);
  makeKey(k_ip, "n%uip", idx);
  makeKey(k_gw, "n%ugw", idx);
  makeKey(k_sn, "n%usn", idx);
  makeKey(k_d1, "n%ud1", idx);
  makeKey(k_d2, "n%ud2", idx);

  gPrefs.putUChar(k_ok, 0);
  gPrefs.remove(k_ip);
  gPrefs.remove(k_gw);
  gPrefs.remove(k_sn);
  gPrefs.remove(k_d1);
  gPrefs.remove(k_d2);
}

// ---------------- Per-AP fast reconnect cache (BSSID+channel) ----------------
static bool loadApCache(uint8_t idx, uint8_t bssidOut[6], uint8_t* chanOut) {
  char k_ok[16], k_ch[16], k_bs[16];
  makeKey(k_ok, "a%uok", idx);
  makeKey(k_ch, "a%uch", idx);
  makeKey(k_bs, "a%ubs", idx);

  uint8_t ok = gPrefs.getUChar(k_ok, 0);
  if (!ok) return false;

  uint8_t ch = gPrefs.getUChar(k_ch, 0);
  if (ch == 0) return false;

  size_t got = gPrefs.getBytes(k_bs, bssidOut, 6);
  if (got != 6) return false;

  *chanOut = ch;
  return true;
}

static void saveApCacheFromCurrentConnection(uint8_t idx) {
  if (WiFi.status() != WL_CONNECTED) return;

  const uint8_t* bssid = WiFi.BSSID();
  uint8_t ch = (uint8_t)WiFi.channel();
  if (!bssid || ch == 0) return;

  char k_ok[16], k_ch[16], k_bs[16];
  makeKey(k_ok, "a%uok", idx);
  makeKey(k_ch, "a%uch", idx);
  makeKey(k_bs, "a%ubs", idx);

  gPrefs.putUChar(k_ok, 1);
  gPrefs.putUChar(k_ch, ch);
  gPrefs.putBytes(k_bs, bssid, 6);

  Serial.print("Cached AP for fast reconnect (idx ");
  Serial.print(idx);
  Serial.print(", ch ");
  Serial.print(ch);
  Serial.println(").");
}

static void clearApCache(uint8_t idx) {
  char k_ok[16], k_ch[16], k_bs[16];
  makeKey(k_ok, "a%uok", idx);
  makeKey(k_ch, "a%uch", idx);
  makeKey(k_bs, "a%ubs", idx);

  gPrefs.putUChar(k_ok, 0);
  gPrefs.remove(k_ch);
  gPrefs.remove(k_bs);
}

// ---------------- Multi-AP selection logic ----------------
static int8_t getLastApIndex() {
  int32_t v = gPrefs.getInt(NVS_LAST_AP, -1);
  if (v < 0 || v >= (int32_t)KNOWN_APS_COUNT) return -1;
  return (int8_t)v;
}

static void setLastApIndex(int8_t idx) {
  if (idx < 0 || idx >= (int8_t)KNOWN_APS_COUNT) return;
  if (getLastApIndex() == idx) return;
  gPrefs.putInt(NVS_LAST_AP, (int)idx);
}

static void bssidToString(const uint8_t bssid[6], char out[18]) {
  snprintf(out, 18, "%02X:%02X:%02X:%02X:%02X:%02X",
           bssid[0], bssid[1], bssid[2], bssid[3], bssid[4], bssid[5]);
  out[17] = 0;
}

static bool configureStaticIfAvailable(uint8_t apIdx, bool* outUsedStatic) {
  if (outUsedStatic) *outUsedStatic = false;

  IPAddress ip, gw, sn, dns1, dns2;
  bool haveStatic = loadNetworkConfigForAp(apIdx, &ip, &gw, &sn, &dns1, &dns2);

  if (haveStatic) {
    bool ok = false;
    if ((uint32_t)dns1 != 0 || (uint32_t)dns2 != 0) ok = WiFi.config(ip, gw, sn, dns1, dns2);
    else ok = WiFi.config(ip, gw, sn);

    Serial.print("Using cached static IP config (AP idx ");
    Serial.print(apIdx);
    Serial.print("): ");
    Serial.println(ok ? "OK" : "FAILED");

    if (ok && outUsedStatic) *outUsedStatic = true;
    return ok;
  }

  WiFi.config(INADDR_NONE, INADDR_NONE, INADDR_NONE);
  Serial.print("No cached network config for AP idx ");
  Serial.print(apIdx);
  Serial.println("; using DHCP.");
  return true;
}

static ScanCandidate scanStrongestKnownCandidate() {
  ScanCandidate best;
  best.apIdx = -1;
  best.rssi = -10000;
  best.channel = 0;
  memset(best.bssid, 0, sizeof(best.bssid));
  best.valid = false;

  int n = WiFi.scanNetworks(/*async=*/false, /*show_hidden=*/true);
  if (n <= 0) {
    WiFi.scanDelete();
    Serial.println("Scan found no networks.");
    return best;
  }

  for (int i = 0; i < n; i++) {
    String s = WiFi.SSID(i);
    int32_t rssi = WiFi.RSSI(i);

    for (uint8_t k = 0; k < KNOWN_APS_COUNT; k++) {
      if (s == KNOWN_APS[k].ssid) {
        // Note: same SSID may appear multiple times on mesh; pick the strongest BSSID.
        if (!best.valid || rssi > best.rssi) {
          best.valid = true;
          best.apIdx = (int8_t)k;
          best.rssi = rssi;

          const uint8_t* b = WiFi.BSSID(i);
          if (b) memcpy(best.bssid, b, 6);

          int32_t ch = WiFi.channel(i);
          best.channel = (ch > 0 && ch < 255) ? (uint8_t)ch : 0;

          char bs[18];
          bssidToString(best.bssid, bs);

          Serial.print("Scan candidate: idx ");
          Serial.print(best.apIdx);
          Serial.print(" (");
          Serial.print(KNOWN_APS[k].ssid);
          Serial.print(") RSSI ");
          Serial.print(best.rssi);
          Serial.print(" dBm, ch ");
          Serial.print(best.channel);
          Serial.print(", BSSID ");
          Serial.println(bs);
        }
      }
    }
  }

  WiFi.scanDelete();

  if (!best.valid) {
    Serial.println("Scan did not find any known SSIDs.");
  }
  return best;
}

// Connect to a known AP. If forceBssid/forceChan are provided (from scan), we connect to that exact BSSID.
// This is critical on mesh systems where the same SSID exists on multiple APs.
static bool connectToApIndex(uint8_t apIdx, const uint8_t* forceBssid = nullptr, uint8_t forceChan = 0, int32_t forceRssi = -10000) {
  if (apIdx >= KNOWN_APS_COUNT) return false;

  const char* ssid = KNOWN_APS[apIdx].ssid;
  const char* pass = KNOWN_APS[apIdx].pass;

  Serial.print("Connecting to AP idx ");
  Serial.print(apIdx);
  Serial.print(" (");
  Serial.print(ssid);
  Serial.println(")");

  // Common post-connect steps (logging + caching).
  auto finishConnected = [&]() -> bool {
    if (WiFi.status() != WL_CONNECTED) return false;

    Serial.print("WiFi OK, IP: ");
    Serial.println(WiFi.localIP());

    // Log the actual BSSID/channel we ended up on (helps diagnose mesh mis-association).
    {
      const uint8_t* b = WiFi.BSSID();
      uint8_t ch = (uint8_t)WiFi.channel();
      int32_t r = WiFi.RSSI();
      if (b) {
        char bs[18];
        bssidToString(b, bs);
        Serial.print("Connected details: RSSI ");
        Serial.print(r);
        Serial.print(" dBm, ch ");
        Serial.print(ch);
        Serial.print(", BSSID ");
        Serial.println(bs);
      } else {
        Serial.print("Connected details: RSSI ");
        Serial.print(r);
        Serial.print(" dBm, ch ");
        Serial.println(ch);
      }
    }

    // Cache BSSID+channel for next wake.
    saveApCacheFromCurrentConnection(apIdx);

    // If we didn't have a cached static config, store DHCP values now.
    // (If we did use cached static config, this call is skipped by the caller.)
    // NOTE: we only call this when usedStatic == false below.

    setLastApIndex((int8_t)apIdx);
    gActiveApIndex = (int8_t)apIdx;
    return true;
  };

  WiFi.disconnect(true, true);
  delay(150);

  bool usedStatic = false;
  configureStaticIfAvailable(apIdx, &usedStatic);

  // 0) If we scanned a specific BSSID+channel, try that first (strongest seen).
  if (forceBssid && forceChan != 0) {
    char bs[18];
    bssidToString(forceBssid, bs);
    Serial.print("Connect using scanned strongest BSSID (RSSI ");
    Serial.print(forceRssi);
    Serial.print(" dBm, ch ");
    Serial.print(forceChan);
    Serial.print(", ");
    Serial.print(bs);
    Serial.println(") ...");

    WiFi.begin(ssid, pass, (int32_t)forceChan, forceBssid, true);
    if (waitForWiFi(WIFI_CONNECT_MS)) {
      Serial.println("Connect (scanned BSSID) OK.");
      // Cache DHCP config if we used it (no cached static).
      if (!usedStatic) saveNetworkConfigFromDhcpForAp(apIdx);
      return finishConnected();
    }
    Serial.println("Connect (scanned BSSID) failed; falling back.");
    WiFi.disconnect(true, true);
    delay(150);
  }

  // 1) Try fast connect (cached BSSID+channel) to skip scanning.
  uint8_t bssid[6];
  uint8_t chan = 0;
  if (loadApCache(apIdx, bssid, &chan)) {
    char bs[18];
    bssidToString(bssid, bs);

    Serial.print("Fast connect using cached BSSID+channel (ch ");
    Serial.print(chan);
    Serial.print(", ");
    Serial.print(bs);
    Serial.println(") ...");

    WiFi.begin(ssid, pass, (int32_t)chan, bssid, true);
    if (waitForWiFi(WIFI_FAST_CONNECT_MS)) {
      Serial.println("Fast connect OK.");
      if (!usedStatic) saveNetworkConfigFromDhcpForAp(apIdx);
      return finishConnected();
    }
    Serial.println("Fast connect failed; trying normal connect.");
    WiFi.disconnect(true, true);
    delay(150);
  }

  // 2) Normal connect (lets the stack pick a BSSID; not ideal on mesh, but kept as fallback).
  WiFi.begin(ssid, pass);
  if (!waitForWiFi(WIFI_CONNECT_MS)) {
    Serial.println("Normal connect failed.");

    // If we tried static and it failed, retry once with DHCP (network may have changed).
    if (usedStatic) {
      Serial.println("Retrying once with DHCP (static may be invalid).");
      WiFi.disconnect(true, true);
      delay(150);
      WiFi.config(INADDR_NONE, INADDR_NONE, INADDR_NONE);
      WiFi.begin(ssid, pass);
      if (!waitForWiFi(WIFI_RETRY_DHCP_MS)) {
        Serial.println("DHCP retry connect failed.");
        return false;
      }
      // Connected via DHCP, cache network config.
      saveNetworkConfigFromDhcpForAp(apIdx);
      return finishConnected();
    }

    return false;
  }

  // Connected (normal connect).
  if (!usedStatic) saveNetworkConfigFromDhcpForAp(apIdx);
  return finishConnected();
}

static bool wifiConnectSmart() {
  if (KNOWN_APS_COUNT == 0) {
    Serial.println("No KNOWN_APS configured.");
    return false;
  }

  WiFi.mode(WIFI_STA);

  // Prevent WiFi library from writing to flash internally.
  WiFi.persistent(false);

  // Apply power tuning.
  WiFi.setTxPower((wifi_power_t)WIFI_TX_POWER);

  // During the short awake window, we prefer fastest association over modem sleep.
  WiFi.setSleep(false);

  // 1) Try last-known-working AP (fast) and keep it if RSSI is "good enough".
  int8_t last = getLastApIndex();
  if (last >= 0) {
    Serial.print("Last-known-working AP idx: ");
    Serial.println(last);

    if (connectToApIndex((uint8_t)last)) {
      int32_t r = WiFi.RSSI();
      Serial.print("Post-connect RSSI: ");
      Serial.print(r);
      Serial.println(" dBm");

      if (r >= WIFI_GOOD_RSSI_DBM) {
        Serial.println("RSSI is good enough; skipping scan.");
        return true;
      }

      Serial.println("RSSI not good enough; disconnecting and searching for a stronger BSSID...");
      WiFi.disconnect(true, true);
      delay(150);
    } else {
      Serial.println("Last-known-working AP failed.");
    }
  } else {
    Serial.println("No last-known-working AP stored.");
  }

  // 2) Scan and connect to the strongest *BSSID* among known SSIDs.
  //    If signal is weak, rescan a few times before giving up (search).
  Serial.println("Searching for strongest known AP (by scan) ...");

  ScanCandidate bestSeen;
  bestSeen.valid = false;
  bestSeen.apIdx = -1;
  bestSeen.rssi = -10000;
  bestSeen.channel = 0;
  memset(bestSeen.bssid, 0, sizeof(bestSeen.bssid));

  for (uint8_t attempt = 1; attempt <= (uint8_t)WIFI_SCAN_ATTEMPTS; attempt++) {
    Serial.print("Scan attempt ");
    Serial.print(attempt);
    Serial.print("/");
    Serial.println((uint8_t)WIFI_SCAN_ATTEMPTS);

    ScanCandidate cand = scanStrongestKnownCandidate();
    if (cand.valid) {
      if (!bestSeen.valid || cand.rssi > bestSeen.rssi) bestSeen = cand;

      if (cand.rssi >= WIFI_GOOD_RSSI_DBM) {
        Serial.println("Found a good enough AP; connecting.");
        if (connectToApIndex((uint8_t)cand.apIdx, cand.bssid, cand.channel, cand.rssi)) return true;
      } else {
        Serial.print("Best RSSI on this scan (");
        Serial.print(cand.rssi);
        Serial.println(" dBm) is below threshold; rescanning...");
      }
    }

    if (attempt < (uint8_t)WIFI_SCAN_ATTEMPTS) delay((uint32_t)WIFI_SCAN_RETRY_DELAY_MS * (uint32_t)attempt);
  }

  // If nothing met the "good enough" threshold, connect to the best we saw anyway.
  if (bestSeen.valid) {
    Serial.println("No good-enough RSSI found; connecting to best available known AP anyway.");
    if (connectToApIndex((uint8_t)bestSeen.apIdx, bestSeen.bssid, bestSeen.channel, bestSeen.rssi)) return true;
  }

  // 3) Final fallback: try all configured APs in order (for hidden SSIDs / scan issues).
  Serial.println("Fallback: trying all configured APs in order...");
  for (uint8_t i = 0; i < KNOWN_APS_COUNT; i++) {
    if ((int8_t)i == last) continue;
    if (connectToApIndex(i)) return true;
  }

  return false;
}

// ---------------- HTTP download with ETag (hostname-based; no DNS caching) ----------------
static void printHttpErrorDetails(HTTPClient& http, int code) {
  Serial.print("HTTP error: ");
  Serial.println(code);
#if defined(ESP32)
  Serial.print("HTTPClient: ");
  Serial.println(http.errorToString(code));
#endif
  String ct = http.header("Content-Type");
  if (ct.length()) {
    Serial.print("Content-Type: ");
    Serial.println(ct);
  }
  // Avoid http.getString() here (can block on chunked/slow bodies). We only log a few bytes if readily available.
  int sz = http.getSize();
  Serial.print("Body size (Content-Length): ");
  Serial.println(sz);
  WiFiClient* s = http.getStreamPtr();
  if (s) {
    s->setTimeout(1);
    char tmp[601];
    size_t n = s->readBytes(tmp, 600);
    tmp[n] = 0;
    if (n > 0) {
      Serial.print("Body (first ");
      Serial.print(n);
      Serial.println(" bytes):");
      Serial.println(tmp);
      return;
    }
  }
  Serial.println("Body: <not read>");
}

static bool httpDownloadAllocETag(
  const String& host,
  uint16_t port,
  bool https,
  const String& uri,
  const String& ifNoneMatchEtag,
  String* outEtag,
  bool* outNotModified,
  uint8_t** outPtr,
  size_t* outLen
) {
  *outPtr = nullptr;
  *outLen = 0;
  *outNotModified = false;
  if (outEtag) *outEtag = "";

  HTTPClient http;
  http.setFollowRedirects(HTTPC_STRICT_FOLLOW_REDIRECTS);
  http.setUserAgent(USER_AGENT);
  http.setTimeout(HTTP_GET_TIMEOUT_MS);

  const char* hdrs[] = {"ETag", "Content-Length", "Last-Modified"};
  http.collectHeaders(hdrs, 3);

  if (https) {
    gTlsClient.setInsecure();

    // IMPORTANT: Client::setTimeout() is in SECONDS.
    gTlsClient.setTimeout((HTTP_GET_TIMEOUT_MS + 999) / 1000);

    if (!http.begin(gTlsClient, host, port, uri, true)) {
      Serial.println("http.begin(secure) failed");
      return false;
    }
  } else {
    if (!http.begin(host, port, uri)) {
      Serial.println("http.begin() failed");
      return false;
    }
  }

  http.addHeader("Accept", "application/octet-stream");
  http.addHeader("Accept-Encoding", "identity");

  if (ifNoneMatchEtag.length()) {
    http.addHeader("If-None-Match", ifNoneMatchEtag);
    Serial.print("If-None-Match: ");
    Serial.println(ifNoneMatchEtag);
  }

  Serial.print("HTTP GET ");
  Serial.println(uri);
  uint32_t tGetStart = millis();
  int code = http.GET();
  Serial.print("HTTP GET done in ");
  Serial.print(millis() - tGetStart);
  Serial.print(" ms, code=");
  Serial.println(code);

  if (code == HTTP_CODE_NOT_MODIFIED) {
    Serial.println("HTTP 304 Not Modified");
    *outNotModified = true;
    if (outEtag) *outEtag = http.header("ETag");
    http.end();
    return true;
  }

  if (code != HTTP_CODE_OK) {
    printHttpErrorDetails(http, code);
    http.end();
    return false;
  }

  if (outEtag) *outEtag = http.header("ETag");

  int len = http.getSize();
  if (len <= 0) {
    Serial.println("Missing/invalid Content-Length; cannot allocate safely.");
    http.end();
    return false;
  }

  Serial.print("Content-Length: ");
  Serial.println(len);

#if defined(ESP32)
  Serial.print("RSSI: ");
  Serial.print(WiFi.RSSI());
  Serial.print(" dBm, free heap: ");
  Serial.println(ESP.getFreeHeap());
#endif

  if (len > 1024 * 1024) {
    Serial.println("Response too large; refusing.");
    http.end();
    return false;
  }

  uint8_t* buf = (uint8_t*)allocGeneral((size_t)len);
  if (!buf) {
    Serial.println("Allocation failed for download buffer.");
    http.end();
    return false;
  }

  WiFiClient* stream = http.getStreamPtr();
  if (!stream) {
    Serial.println("No stream ptr");
    http.end();
    freeBuffer(buf);
    return false;
  }

  // IMPORTANT: setTimeout is in SECONDS, not ms
  stream->setTimeout((STREAM_READ_BLOCK_TIMEOUT_MS + 999) / 1000);

  size_t total = 0;
  const uint32_t t0 = millis();
  uint32_t lastProgress = millis();
  uint32_t lastLog = millis();

  while (total < (size_t)len) {
    // Non-blocking fast path: read what is already decrypted/buffered.
    int avail = stream->available();

    if (avail <= 0) {
      // No bytes ready right now. Avoid blocking. Let WiFi tasks run.
      if (millis() - lastProgress > DOWNLOAD_STALL_TIMEOUT_MS) {
        Serial.print("Download stalled > ");
        Serial.print(DOWNLOAD_STALL_TIMEOUT_MS);
        Serial.print(" ms at ");
        Serial.print(total);
        Serial.print("/");
        Serial.println(len);
        break;
      }
      if (millis() - t0 > DOWNLOAD_OVERALL_TIMEOUT_MS) {
        Serial.print("Download overall timeout > ");
        Serial.print(DOWNLOAD_OVERALL_TIMEOUT_MS);
        Serial.print(" ms at ");
        Serial.print(total);
        Serial.print("/");
        Serial.println(len);
        break;
      }
      delay(1);
      yield();
      continue;
    }

    size_t remaining = (size_t)len - total;
    size_t want = remaining;

    // Limit by both our chunk size and what's currently buffered.
    if (want > (size_t)DOWNLOAD_CHUNK_BYTES) want = (size_t)DOWNLOAD_CHUNK_BYTES;
    if ((size_t)avail < want) want = (size_t)avail;

    int r = stream->read((uint8_t*)buf + total, want);

    if (r > 0) {
      // Safety clamps (paranoid, but prevents overruns even with buggy stacks)
      if ((size_t)r > want) r = (int)want;
      if (total + (size_t)r > (size_t)len) r = (int)((size_t)len - total);

      total += (size_t)r;
      lastProgress = millis();
    }

    if (millis() - lastLog > 2000) {
      Serial.print("DL ");
      Serial.print(total);
      Serial.print("/");
      Serial.print(len);
      Serial.print(" bytes (elapsed ");
      Serial.print(millis() - t0);
      Serial.println(" ms)");
      lastLog = millis();
    }

    yield();
  }

  http.end();

  if (total < (size_t)len) {
    Serial.print("Download incomplete: got ");
    Serial.print(total);
    Serial.print(" expected ");
    Serial.println(len);
    freeBuffer(buf);
    return false;
  }

  *outPtr = buf;
  *outLen = (size_t)len;
  return true;
}

// ---------------- Format detection ----------------
static InFormat detectFormat(size_t inLen) {
  if (inLen == PIXELS / 4) return IN_2BPP_PACKED;
  if (inLen == PIXELS / 2) return IN_4BPP_PACKED;
  if (inLen == PIXELS)     return IN_8BPP_RAW;
  return IN_UNKNOWN;
}

// ---------------- Conversion (to 4bpp packed, our target) ----------------
static void convert_8bpp_to_4bpp_full(const uint8_t* in8, uint8_t* out4, bool looks2, bool looks4) {
  const size_t outStride = WIDTH / 2;
  for (uint16_t y = 0; y < HEIGHT; y++) {
    if ((y & 0x0F) == 0) yield();
    const size_t inRow  = (size_t)y * (size_t)WIDTH;
    const size_t outRow = (size_t)y * outStride;
    size_t o = outRow;
    for (uint16_t x = 0; x < WIDTH; x += 2) {
      uint8_t v0 = in8[inRow + x + 0];
      uint8_t v1 = in8[inRow + x + 1];

      uint8_t p0, p1;
      if (looks2) { p0 = (uint8_t)((v0 & 0x03) * 5); p1 = (uint8_t)((v1 & 0x03) * 5); }
      else if (looks4) { p0 = (v0 > 15) ? 15 : v0; p1 = (v1 > 15) ? 15 : v1; }
      else { p0 = (uint8_t)((v0 * 15u + 127u) / 255u); p1 = (uint8_t)((v1 * 15u + 127u) / 255u); }

      p0 = invert4(p0);
      p1 = invert4(p1);
      out4[o++] = PACK_NIBBLES(p0, p1);
    }
  }
}

static void convert_4bpp_to_4bpp_full(const uint8_t* in4, uint8_t* out4) {
  const size_t bytes = (size_t)WIDTH * (size_t)HEIGHT / 2;
  for (size_t i = 0; i < bytes; i++) {
    if ((i & 0x3FFF) == 0) yield();
    uint8_t b  = in4[i];
    uint8_t hi = (b >> 4) & 0x0F;
    uint8_t lo = (b >> 0) & 0x0F;
    hi = invert4(hi);
    lo = invert4(lo);
    out4[i] = PACK_NIBBLES(hi, lo);
  }
}

static void convert_2bpp_to_4bpp_full(const uint8_t* in2, uint8_t* out4) {
  const size_t inStride  = WIDTH / 4;
  const size_t outStride = WIDTH / 2;
  auto get2 = [&](uint16_t x, const uint8_t* row) -> uint8_t {
    uint8_t b  = row[x >> 2];
    uint8_t sh = (uint8_t)(6 - 2 * (x & 3));
    return (b >> sh) & 0x03;
  };

  for (uint16_t y = 0; y < HEIGHT; y++) {
    if ((y & 0x0F) == 0) yield();
    const uint8_t* inRowPtr = in2 + (size_t)y * inStride;
    uint8_t* outRowPtr      = out4 + (size_t)y * outStride;
    size_t o = 0;
    for (uint16_t x = 0; x < WIDTH; x += 2) {
      uint8_t v0 = get2(x + 0, inRowPtr);
      uint8_t v1 = get2(x + 1, inRowPtr);
      uint8_t p0 = invert4((uint8_t)(v0 * 5));
      uint8_t p1 = invert4((uint8_t)(v1 * 5));
      outRowPtr[o++] = PACK_NIBBLES(p0, p1);
    }
  }
}

// ---------------- Tiled fallback helpers ----------------
static void fillTile_8bpp_to_4bpp(const uint8_t* in8, uint8_t* tileBuf, uint16_t y0, uint16_t h, bool looks2, bool looks4) {
  const size_t inStride  = WIDTH;
  const size_t outStride = WIDTH / 2;

  for (uint16_t yy = 0; yy < h; yy++) {
    if ((yy & 0x07) == 0) yield();
    const size_t inRow  = (size_t)(y0 + yy) * inStride;
    const size_t outRow = (size_t)yy * outStride;

    size_t outIdx = outRow;
    for (uint16_t x = 0; x < WIDTH; x += 2) {
      uint8_t v0 = in8[inRow + x + 0];
      uint8_t v1 = in8[inRow + x + 1];

      uint8_t p0, p1;
      if (looks2) {
        p0 = (uint8_t)((v0 & 0x03) * 5);
        p1 = (uint8_t)((v1 & 0x03) * 5);
      } else if (looks4) {
        p0 = (v0 > 15) ? 15 : v0;
        p1 = (v1 > 15) ? 15 : v1;
      } else {
        p0 = (uint8_t)((v0 * 15u + 127u) / 255u);
        p1 = (uint8_t)((v1 * 15u + 127u) / 255u);
      }

      p0 = invert4(p0);
      p1 = invert4(p1);

      tileBuf[outIdx++] = PACK_NIBBLES(p0, p1);
    }
  }
}

static void fillTile_4bppPacked_to_4bppPacked(const uint8_t* in4, uint8_t* tileBuf, uint16_t y0, uint16_t h) {
  const size_t stride = WIDTH / 2;

  for (uint16_t yy = 0; yy < h; yy++) {
    if ((yy & 0x07) == 0) yield();
    const size_t srcRow = (size_t)(y0 + yy) * stride;
    const size_t dstRow = (size_t)yy * stride;

    for (size_t i = 0; i < stride; i++) {
      uint8_t b  = in4[srcRow + i];
      uint8_t hi = (b >> 4) & 0x0F;
      uint8_t lo = (b >> 0) & 0x0F;

      hi = invert4(hi);
      lo = invert4(lo);

      tileBuf[dstRow + i] = PACK_NIBBLES(hi, lo);
    }
  }
}

static void fillTile_2bppPacked_to_4bppPacked(const uint8_t* in2, uint8_t* tileBuf, uint16_t y0, uint16_t h) {
  const size_t inStride  = WIDTH / 4;
  const size_t outStride = WIDTH / 2;

  auto get2 = [&](uint16_t x, const uint8_t* row) -> uint8_t {
    uint8_t b  = row[x >> 2];
    uint8_t sh = (uint8_t)(6 - 2 * (x & 3));
    return (b >> sh) & 0x03;
  };

  for (uint16_t yy = 0; yy < h; yy++) {
    if ((yy & 0x07) == 0) yield();
    const uint8_t* inRowPtr = in2 + (size_t)(y0 + yy) * inStride;
    uint8_t* outRowPtr      = tileBuf + (size_t)yy * outStride;

    size_t outIdx = 0;
    for (uint16_t x = 0; x < WIDTH; x += 2) {
      uint8_t v0 = get2(x + 0, inRowPtr);
      uint8_t v1 = get2(x + 1, inRowPtr);

      uint8_t p0 = invert4((uint8_t)(v0 * 5));
      uint8_t p1 = invert4((uint8_t)(v1 * 5));

      outRowPtr[outIdx++] = PACK_NIBBLES(p0, p1);
    }
  }
}

// ---------------- Display: full-frame if possible, else tiled fallback ----------------
static bool shouldFullClearNow() {
  if (gDrawCount == 0) return true;               // cold start / first draw after power cycle
  if (FULL_CLEAR_EVERY_N_UPDATES == 0) return false;
  return (gDrawCount % FULL_CLEAR_EVERY_N_UPDATES) == 0;
}

static void displayFullOrTiled(InFormat fmt, bool doFullClear) {
  const size_t fullBytes = (size_t)WIDTH * (size_t)HEIGHT / 2;

  bool looks2 = false, looks4 = false;
  if (fmt == IN_8BPP_RAW) {
    uint8_t maxv = 0;
    const size_t step = (PIXELS >= 4096) ? (PIXELS / 4096) : 1;
    for (size_t i = 0; i < PIXELS; i += step) {
      uint8_t v = gInBuf[i];
      if (v > maxv) maxv = v;
    }
    looks2 = (maxv <= 3);
    looks4 = (!looks2 && maxv <= 15);
  }

  uint8_t* fullBuf = (uint8_t*)allocDMABuffer(fullBytes);

  epd_init();
  epd_poweron();
  if (doFullClear) {
    Serial.println("EPD: full clear");
    epd_clear();
  } else {
    Serial.println("EPD: skip clear");
  }

  if (fullBuf) {
    Serial.println("Rendering full-frame (single call).");

    yield(); // keep WDT/WiFi tasks happy before heavy CPU work

    if (fmt == IN_8BPP_RAW)         convert_8bpp_to_4bpp_full(gInBuf, fullBuf, looks2, looks4);
    else if (fmt == IN_4BPP_PACKED) convert_4bpp_to_4bpp_full(gInBuf, fullBuf);
    else if (fmt == IN_2BPP_PACKED) convert_2bpp_to_4bpp_full(gInBuf, fullBuf);

    Rect_t area;
    area.x = 0;
    area.y = 0;
    area.width  = WIDTH;
    area.height = HEIGHT;
    epd_draw_grayscale_image(area, fullBuf);

    yield();

    epd_poweroff();
    freeBuffer(fullBuf);
    return;
  }

  Serial.println("Full-frame DMA alloc failed; rendering tiled fallback.");
  const size_t tileBytes = (size_t)WIDTH * (size_t)TILE_H / 2;
  uint8_t* tileBuf = (uint8_t*)allocDMABuffer(tileBytes);
  if (!tileBuf) {
    Serial.println("Failed to allocate tile DMA buffer.");
    epd_poweroff();
    while (true) delay(1000);
  }

  for (uint16_t y = 0; y < HEIGHT; y += TILE_H) {
    uint16_t h = (uint16_t)min((uint16_t)TILE_H, (uint16_t)(HEIGHT - y));

    if (fmt == IN_8BPP_RAW)         fillTile_8bpp_to_4bpp(gInBuf, tileBuf, y, h, looks2, looks4);
    else if (fmt == IN_4BPP_PACKED) fillTile_4bppPacked_to_4bppPacked(gInBuf, tileBuf, y, h);
    else if (fmt == IN_2BPP_PACKED) fillTile_2bppPacked_to_4bppPacked(gInBuf, tileBuf, y, h);

    Rect_t area;
    area.x = 0;
    area.y = y;
    area.width  = WIDTH;
    area.height = h;
    epd_draw_grayscale_image(area, tileBuf);

    yield();
  }

  epd_poweroff();
  freeBuffer(tileBuf);
}

// ---------------- Refresh logic ----------------
static void updateEtagCaches(const String& newEtag) {
  if (!newEtag.length()) return;

  // Always keep RTC updated to avoid frequent flash writes.
  if (gCachedEtag != newEtag) {
    gCachedEtag = newEtag;
    rtcSetEtag(newEtag);
    gRtcEtagChangeCount++;
    maybePersistEtagToFlash();
  } else {
    // Ensure RTC has it even if flash/RTC got out of sync.
    rtcSetEtag(newEtag);
  }
}

static bool refreshOnce(bool* outChanged) {
  if (outChanged) *outChanged = false;

  uint8_t* buf = nullptr;
  size_t len = 0;
  bool notModified = false;
  String newEtag;

  const String uriWithBattery = appendDeviceBatteryToUri(gUrlUri, gDeviceBattery);
  Serial.print("Refresh: GET ");
  Serial.println(uriWithBattery);

  bool ok = httpDownloadAllocETag(
    gUrlHost, gUrlPort, gUrlHttps, uriWithBattery,
    gCachedEtag, &newEtag, &notModified, &buf, &len
  );
  if (!ok) return false;

  Serial.print("Refresh: download OK, bytes=");
  Serial.print(len);
  if (newEtag.length()) {
    Serial.print(", ETag=");
    Serial.println(newEtag);
  } else {
    Serial.println(", ETag=<empty>");
  }

  // Update ETag caches even on 304 (some servers return it).
  updateEtagCaches(newEtag);

  if (notModified) {
    Serial.println("No change; skipping display update.");
    if (outChanged) *outChanged = false;
    return true;
  }

  InFormat fmt = detectFormat(len);
  if (fmt == IN_UNKNOWN) {
    Serial.println("Unsupported payload size.");
    freeBuffer(buf);
    return false;
  }

  Serial.print("Payload format: ");
  Serial.println((int)fmt);

  gInBuf = buf;
  gInLen = len;

  // Turn off WiFi/BT before driving the EPD.
  // This reduces peak current and avoids rare stalls/resets during long updates (especially on battery).
  Serial.println("WiFi: off before display");
  powerDownRadios();

  const bool doClear = shouldFullClearNow();
  Serial.println("Display: start");
  uint32_t tDisp = millis();
  displayFullOrTiled(fmt, doClear);
  Serial.print("Display: done in ");
  Serial.print(millis() - tDisp);
  Serial.println(" ms");

  gDrawCount++;

  freeBuffer(buf);
  gInBuf = nullptr;
  gInLen = 0;

  if (outChanged) *outChanged = true;
  return true;
}

// ---------------- Arduino setup/loop ----------------
void setup() {
  // Apply CPU frequency early (power saving).
#if defined(ESP32)
  setCpuFrequencyMhz(CPU_FREQ_MHZ);
#endif

  Serial.begin(115200);
  delay(200);
  Serial.println();

  printBootInfo();

  gPrefs.begin(NVS_NS, false);

  batteryInit();
  gDeviceBattery = readBatteryPercentOnce();

  Serial.print("Battery: ");
  Serial.print(gDeviceBattery);
  Serial.println("%");

  if (!parseUrl(IMAGE_URL, &gUrlHttps, &gUrlHost, &gUrlPort, &gUrlUri)) {
    Serial.println("URL parse failed.");
    deepSleepSeconds(SLEEP_FAIL_S);
    return;
  }

  // Prefer RTC ETag across deep sleep to avoid flash writes; fall back to NVS on cold boot.
  if (gRtcEtagValid && gRtcEtag[0]) {
    gCachedEtag = String(gRtcEtag);
    Serial.print("Loaded RTC ETag: ");
    Serial.println(gCachedEtag);
  } else {
    gCachedEtag = gPrefs.getString(NVS_ETAG, "");
    if (gCachedEtag.length()) {
      Serial.print("Loaded flash ETag: ");
      Serial.println(gCachedEtag);
      rtcSetEtag(gCachedEtag);
    } else {
      Serial.println("No cached ETag yet.");
    }
  }

  if (!wifiConnectSmart()) {
    Serial.println("WiFi connect failed; sleeping 60s.");
    deepSleepSeconds(SLEEP_FAIL_S);
    return;
  }

  Serial.println("Refreshing image...");
  bool changed = false;
  bool ok = refreshOnce(&changed);

  if (!ok) {
    Serial.println("Refresh failed; sleeping 60s.");

    // Targeted cache clearing for the active AP:
    if (gActiveApIndex >= 0) {
      clearNetworkCacheForAp((uint8_t)gActiveApIndex); // forces DHCP next wake for same AP
      clearApCache((uint8_t)gActiveApIndex);           // forces scan-based connect (or normal connect)
    }

    deepSleepSeconds(SLEEP_FAIL_S);
    return;
  }

  deepSleepSeconds(changed ? SLEEP_CHANGED_S : SLEEP_SAME_S);
}

void loop() {
  delay(1000);
}
