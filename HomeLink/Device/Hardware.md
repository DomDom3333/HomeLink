# HomeLink – Hardware Shopping List

## Required Components

### 1. LilyGo T5 4.7" E-Ink Development Board
- **MCU:** ESP32-S3
- **Display:** 960 × 540 px e-ink panel, 4-bit grayscale (16 shades)
- **Purchase:** [Amazon.de – LILYGO ESP32-S3 Development Display](https://www.amazon.de/-/en/LILYGO-ESP32-S3-Development-Display-Version/dp/B0BWDV4873)

---

### 2. 8 000 mAh LiPo Battery
- **Connector:** PH2.0
- **Purchase:** [Amazon.de – YELUFT 8000mAh LiPo with PH2.0](https://www.amazon.de/-/en/YELUFT-Integrated-Protection-Compatible-Meshtastic/dp/B0F4WW11PX)

> ⚠️ **Critical – connector polarity:** This specific battery has its PH2.0 header poles **swapped** from the standard pinout. You **must** re-pin or swap the connector before connecting it to the board, or you risk damaging the device.

> ⚠️ **Dimensions:** This battery is a very tight fit inside the 3-D printed case. If you substitute a different battery, verify its dimensions against the case model before ordering.

---

## Assembly Notes

- **Insert the battery first.** The EInk display is a tight fit and can catch on any imperfections in the print. To reduce stress on the display ribbon, seat the battery in the case before installing the display.
- **Expect some internal flex** when pressing the battery into place — this is normal for the current case design.
- **The sliding door is a tight fit.** The USB-C connector sits slightly in the way, so closing the lid requires a little firm but gentle pressure — don't force it hard, just steady and even until it clicks into place.
