# Razer Nari Ultimate — USB HID control protocol

Reverse-engineered from Razer Synapse 4 (RazerAppEngine `app-4.0.896`) and verified on hardware
(headset firmware `0106,client`, dongle firmware `0106,host`, Windows 11 26100).

Synapse 4 does not ship the device logic in its installer. It downloads a per-product web module
from `https://apps.razer.com/synapse/products/1306/{mw,ui}/` and caches it in
`%LocalAppData%\Razer\RazerAppEngine\User Data\Default\Service Worker\CacheStorage\…`.
The module is codenamed **`project-anne-hopet1`** ("Hope T1", manifest `short_name: "Hopet1_MW"`).
Files used for this document (webpack chunk hashes identify the exact build):

| File | Contents |
|---|---|
| `mw/main.25659995f9ac02a8bfab.js` | device descriptor, default profile, feature flags (`rzDeviceType = "AudioAvneraDevice"`) |
| `mw/AudioAvneraDevice.80e1dc570f6f4aab0767.js` | the device class: transport (module 22455), command functions (33688), command table (77337), enums (79949) |
| `mw/rzHardwareEventsAudioAvneraParser.6cbcb8102a90d87e3823.js` | input-report (event) parser |
| `mw/3487.6f6624563a3fedb88bbc.js` | shared middleware: task runners, event handlers, enums (module 89846) |

Every request/response layout below was checked against Synapse's own logs
(`%LocalAppData%\Razer\RazerAppEngine\User Data\Logs\products_1306_mw *.log`, which record each
command as `sendCommandOut result {…"command":[…],"commandDesc":"…"}`) and, for every read command,
by sending it to the headset (see §8).

Notation: bytes are hex. "UNCERTAIN" marks anything inferred rather than proven by code or capture.

Which Synapse features are handled by the headset and which are PC-side audio processing (THX APO)
is summarised in [NARI_ULTIMATE.md](NARI_ULTIMATE.md); the PC side is in
[NARI_ULTIMATE_AUDIO.md](NARI_ULTIMATE_AUDIO.md). This file covers only what goes over USB HID.

---

## 1. Device identification

| Item | Value |
|---|---|
| USB VID / PID | `1532` / `051A` (decimal 5426 / 1306) — the USB dongle ("dock") |
| Synapse device class | `AudioAvneraDevice` (Avnera wireless audio SoC) |
| Edition ids accepted by the module | `0`, `128`, `129` (manifest `editionId`) |
| USB interfaces | `MI_00` audio "Razer Nari Ultimate - Chat", `MI_03` audio "Razer Nari Ultimate - Game", `MI_05` HID |
| HID interface | `MI_05` (Synapse descriptor: `reportIDs: {5: "hardwareEventReportID"}` — key 5 = interface 5) |

The dongle is always enumerated, whether or not the headset is on. Commands addressed to the
headset only work while the wireless link is up (§5.1).

### 1.1 HID collections on MI_05 (read with `HidP_GetCaps` / `HidP_Get{Value,Button}Caps`)

| Collection | Usage page / usage | Report lengths (in/out/feature) | Reports | Used for |
|---|---|---|---|---|
| `COL01` | `0x000C / 0x0001` Consumer Control | 5 / 0 / 0 | in `0x01`: buttons `0xE2` Mute, `0xEA` Vol−, `0xE9` Vol+, `0xB5` Next, `0xCD` Play/Pause, `0xB6` Previous | headset volume wheel / buttons; handled natively by Windows |
| `COL02` | `0xFFC0 / 0x0001` vendor | 2 / 0 / 0 | in `0x64` (4-bit value), in `0x65` (3 bits, usages `0x15`–`0x17`) | **events**: link status, mic mute (§6) |
| `COL03` | `0xFF00 / 0x0001` vendor | 64 / 64 / 64 | feature `0xFF`; in `0xFF`, `0x90`, `0xC4`, `0xE2`; out `0x90`, `0xC4` | **command channel** (feature report `0xFF`). The other reports are not used by Synapse (UNCERTAIN: Avnera's own tooling / firmware update) |

Open `COL03` with `GENERIC_READ | GENERIC_WRITE` and `FILE_SHARE_READ | FILE_SHARE_WRITE`. Synapse
does not claim the device exclusively; another program can talk to it while Synapse runs (it did
during the verification in §8). `HidD_GetInputReport` on `0x64`/`0x65` fails (error 31): those reports
are interrupt-IN only and must be read with `ReadFile` / overlapped I/O on `COL02`.

---

## 2. Transport

All commands are **HID feature reports with report id `0xFF`** on `COL03`, 64 bytes including the
report id (`FeatureReportByteLength = 64`).

`AudioAvneraDevice.createDataSend(command, payload)`:
```js
const n = new Uint8Array(64);
n[0] = 255, n[1] = 10;                    // FF 0A 00
let i = 3;
for (let t = 0; t < e.length; t++) n[t + i] = e[t];   // command template
i = 3 + e.length;
for (let e = 0; e < t.length; e++) n[e + i] = t[e];   // payload (set commands)
return n
```
`sendCommandOut` (Electron path) then calls `hid.sendFeatureReport(dataSend)` (must return 64 = bytes
written) and, unless the call is fire-and-forget, polls `hid.getFeatureReport(reportId 255)`:

```js
for (let e = 0; e < this.maxRetryIn; e++) {           // maxRetryIn = 10
  yield sleep(100);
  let t = yield getFeatureReport(255, this.reportSize /* 65 */);
  // match the expected reply header at offset 3
  for (let e = 0; e < i.length; e++) if (t[e + 3] !== i[e]) { mismatch = true; break }
  if (!mismatch) {
    const e = i.byteLength + 3;
    o.packetSizeReturned = i[4] - (i.byteLength - 4);
    o.data = [...t.slice(e, e + o.packetSizeReturned)];   // SUCCESS
    return o
  }
}
```
- A request is written once; the reply is read back by `GetFeature(0xFF)` after ~100 ms, up to
  10 times, until the bytes at offset 3 match the expected reply header (§3.2). Anything else in the
  report buffer (an older reply, or a reply to another program's request) is ignored.
- `maxRetryOut = 20`: if the write fails or no matching reply arrives, the whole write + poll cycle
  is repeated (10 ms gap). Defaults (`main.js` module 85633): `sleepTimeBetweenOut = 5`,
  `sleepTimeBetweenOutIn = 5`, `sleepTimeBetweenIn = 5` ms, `maxRetryIn = 10`, `maxRetryOut = 20`.
- **Set commands are fire-and-forget**: they are sent with `waitForReply = false`, so Synapse never
  reads a reply for them. It reports success as soon as `SetFeature` succeeds and echoes the payload
  as `data` (that is why the logs show e.g. `"data":[192]` for Set Side Tone).
- All commands are serialised with a Web Lock named `AudioAvneraDevice-<containerId>`
  (`useLock = true`). Do the same: one request in flight at a time.
- Connect probe: on open Synapse passes `queryData = createDataSend(Get Extended Firmware Version)`
  to `connectHidDevice` and retries the open 3 times.

The WebUSB fallback in the same class (not used by the Electron app) shows the raw USB form:
`SET_REPORT` class/interface request `0x09`, `wValue = 0x03FF` (feature, id `0xFF`),
`wIndex = <interface>`, 64-byte data stage.

---

## 3. Frame format

### 3.1 Request (written at report offset 3)
```
report: FF 0A 00 | op a1 a2 node len body…  (zero padded to 64 bytes)
```
| Field | Values |
|---|---|
| `op` | `FD` = read request, `FF` = write request (`FE` = reply, §3.2) |
| `a1 a2` | `04 mm` in requests: `04` = host side, `mm` = target module (`00`, `02`, `07`, `0E`, `12` seen) |
| `node` | `F1` = headset (over the wireless link), `00` = USB dongle ("dock") |
| `len` | number of bytes from `len` to the end of the frame, **inclusive** (frame length = 4 + `len`) |
| `body` | for reads: the register / sub-command id (plus address and count for memory reads); for writes: id + value bytes |

Report bytes 1–2 are always `0A 00` in requests. Their meaning is unknown (UNCERTAIN); reproduce them.

### 3.2 Reply (read with `GetFeature(0xFF)`)
```
report: FF nn 05 | FE a1 a2 node len data…
```
| Field | Values |
|---|---|
| `nn` | number of valid bytes in the report **including** the report id = `3 + 4 + len`. Bytes after that are stale buffer contents from older replies |
| report[2] | always `05` in every capture (UNCERTAIN meaning) |
| `FE` | reply |
| `a1 a2` | swapped: `mm 04` (module, then host) |
| `node` | `1F` = from headset (request node `F1`), `0F` = from dongle (request node `00`) |
| `len` | as in the request: bytes from `len` to frame end inclusive |
| `data` | the read id is echoed first, then the value (exception: Get Extended Firmware Version has no echo) |

Synapse stores, for each read, the expected reply header (`op a1 a2 node len` + echoed id bytes) and
returns `data = frame[header.length …]`, with `len − (header.length − 4)` bytes.

Example, Get Battery Status (captured on hardware):
```
→ FF 0A 00  FD 04 12 F1 02 05
← FF 0F 05  FE 12 04 1F 08 05 | 03 05 01 0F 30 50
            └ reply header ───┘  └ data (6 bytes) ┘
```

### 3.3 Memory access (module `00`)
Some values are read straight out of device memory:
```
FD 04 00 <node> 05 81 <addr lo> <addr hi> <count>      read <count> bytes at 16-bit address
← FE 00 04 <node'> <len> 81 <addr lo> <addr hi> <data × count>
```
Serial number and edition are copied from non-volatile storage into RAM first, then read back. Each
is a fixed sequence of three frames sent back to back; only the last one is answered (§4.2). My reading
of the copy frame is `00 | src u32 LE | dst u32 LE | count u16 LE`, followed by `FF … 02 84` (execute).
This is UNCERTAIN: **replay the sequences verbatim**.

---

## 4. Commands

### 4.0 Summary

All rows are taken from module 77337 (`AudioAvneraDevice.js`). "Reply header" is what Synapse matches.
Column ✔ = verified by sending the read to the headset (§8); "log" = seen in Synapse's logs.

| Name (`commandDesc`) | Request (after `FF 0A 00`) | Reply header | Data | ✔ |
|---|---|---|---|---|
| Get Link Status (dongle) | `FD 04 00 00 05 81 AA C6 01` | `FE 00 04 0F 05 81 AA C6` | 1 | ✔ |
| Get Battery Status | `FD 04 12 F1 02 05` | `FE 12 04 1F 08 05` | 6 | ✔ |
| Get Extended Firmware Version (headset) | `FD 04 07 F1 05 02 05 00 00` | `FE 07 04 1F 14` | 19 | ✔ |
| Get Dock Extended Firmware Version | `FD 04 07 00 05 02 05 00 00` | `FE 07 04 0F 14` | 19 | ✔ |
| Get Serial Number (headset) | 3 frames, §4.2 | `FE 00 04 1F 14 81 A0 B0` | 16 | log |
| Get Edition Information (headset) | 3 frames, §4.2 | `FE 00 04 1F 05 81 A0 B0` | 1 | log |
| Get Dock Serial Number | 3 frames, §4.2 | `FE 00 04 0F 14 81 60 C0` | 16 | – |
| Get Dock Edition Information | 3 frames, §4.2 | `FE 00 04 0F 05 81 60 C0` | 1 | – |
| Get Side Tone | `FD 04 00 F1 05 81 60 B4 01` | `FE 00 04 1F 05 81 60 B4` | 1 | ✔ |
| Set Side Tone | `FF 04 0E F1 05 01 04 00` + `vv` | – | – | log |
| Get Silence Detection Control (= auto power-off) | `FD 04 12 F1 02 41` | `FE 12 04 1F 05 41` | 3 | ✔ |
| Set Silence Detection Control | `FF 04 12 F1 05 41` + `en lo hi` | – | – | log |
| Get Haptics Control | `FD 04 02 F1 03 20 00` | `FE 02 04 1F 06 20` | 4 | ✔ |
| Set Haptics Control | `FF 04 02 F1 06 20` + `en lo hi` (+ `00`) | – | – | log |
| Get Chroma Effect | `FD 04 12 F1 02 70` | `FE 12 04 1F 06 70` | 4 | ✔ |
| Set Chroma Effect | `FF 04 12 F1 06 70` + `ee r g b` | – | – | code |
| Get Chroma Brightness | `FD 04 12 F1 02 71` | `FE 12 04 1F 03 71` | 1 | ✔ |
| Set Chroma Brightness | `FF 04 12 F1 03 71` + `bb` | – | – | log |
| Set Chroma Custom Frame | `FF 04 12 F1 05 72` + `r g b` | – | – | code |

There are no other commands in this device class. Synapse has no HID command for EQ, bass boost,
normalization, voice clarity, spatial audio, mic gain/EQ/noise reduction, or game/chat balance: those
are all PC-side (see [NARI_ULTIMATE_AUDIO.md](NARI_ULTIMATE_AUDIO.md)).

### 4.1 Link status
- `FD 04 00 00 05 81 AA C6 01`. This is a memory read on the **dongle** (node `00`), 1 byte at `0xC6AA`.
- Value = `data[0]`, enum `79949.U` / `89846.XF`:

| Value | Name | Meaning |
|---|---|---|
| `1` | Active | headset on and linked; headset commands work |
| `2` | Pair | pairing |
| `3` | Searching | headset off / out of range |
| `5` | ReSync | re-syncing |
| `6` | InitialScan | dongle still starting up (Synapse waits: polls every 1 s while `== 6` or the read fails) |
| `8` | PairCancel | |
| `9` | ActiveSearch | |
| `11` | ActivePair | |

- `getPowerOnStatusCommon()` = `linkStatus == 1`. Synapse polls this constantly (30 479 entries in the
  logs). It also listens for the `0x64` event (§6) so it doesn't have to poll so often.

### 4.2 Identity: firmware, serial number, edition
**Extended firmware version**, headset `FD 04 07 F1 05 02 05 00 00`, dongle `FD 04 07 00 05 02 05 00 00`:
- `data[0] == 0x02`, then ASCII. The headset (captured) gives `"0106,client,2018-1"`
  and the dongle `"0106,host,2018-10-"`. The 19-byte reply cuts off the build date.
- Synapse: `major = data[1]-'0'`, `minor = data[2]-'0'`, `internal = data[3]-'0'`,
  `reserved = data[4]-'0'` → displayed `"0.1.0.6"`. It checks `data[0] == 2` before parsing.

**Serial number**, headset (three frames, sent in this order, reply only to the last):
```
FF 04 00 F1 0C 00 3B B5 00 7F 00 00 A0 B0 10 00     copy 16 bytes NVM 0x7F00B53B → RAM 0xB0A0 (UNCERTAIN reading)
FF 04 00 F1 02 84                                   execute
FD 04 00 F1 05 81 A0 B0 10                          read 16 bytes at 0xB0A0
← FE 00 04 1F 14 81 A0 B0 | 16 bytes
```
- Logged reply data: `48 4E 32 31 34 31 44 30 39 30 30 30 32 30 31 00` = `"HN2141D09000201"`.
  Synapse builds the serial from `data[1..22]`, keeping only printable characters (32–126), so the
  displayed serial is `N2141D09000201`: the leading `H` is skipped (UNCERTAIN: probably a "headset"
  marker).

**Edition**, headset: same pattern with `00 3B B5 10 7F 00 00 A0 B0 01 00` (copy 1 byte) and
`FD 04 00 F1 05 81 A0 B0 01`. `edition = data[0]` (logged `0`). In the logs this read sometimes fails
with "command unknown". Synapse simply continues; retry it.

**Dongle ("dock")** serial and edition: node `00` with copy source `2E C4 00 7F` / `2E C4 10 7F`,
RAM `60 C0`:
```
serial : FF 04 00 00 0C 00 2E C4 00 7F 00 00 60 C0 10 00 | FF 04 00 00 02 84 | FD 04 00 00 05 81 60 C0 10
edition: FF 04 00 00 0C 00 2E C4 10 7F 00 00 60 C0 01 00 | FF 04 00 00 02 84 | FD 04 00 00 05 81 60 C0 01
```
Defined in the class, but no Nari code path calls them.

### 4.3 Battery
- `FD 04 12 F1 02 05` → 6 data bytes. Synapse uses only two of them:
```js
level: e.data[5],
isCharging: 5 === e.data[0],
chargingStatus: 5 === e.data[0] ? "Charging" : "NoCharge_BatteryFull"
```
- Full layout. This is inferred from 4 625 logged replies plus a live capture; only `[0]==5` and `[5]`
  are used by Synapse:

| Byte | Meaning | Observed |
|---|---|---|
| `[0]` | charge state | `3` on battery, `5` charging, `6` charged on cable (seen only at 100 %) — UNCERTAIN for `6` |
| `[1]` | ? | `5` on battery; `3` or `4` while charging |
| `[2]` | battery state | `1` normal, `2` low (only at 30 %), `5` charging, `6` full (UNCERTAIN) |
| `[3..4]` | battery voltage, **mV, big-endian** (UNCERTAIN, but tracks the level) | 3544–3600 @30 %, 3608–3744 @50 %, 3752–4120 @80 %, 4000–4160 @100 %, 4080–4280 charging |
| `[5]` | battery level % | only coarse steps were ever reported: **30, 50, 80, 100** |

- Live capture: `03 05 01 0F 30 50` = on battery, 3888 mV, 80 %, matching Synapse's "80 %".
- The percentage is coarse and has hysteresis (80/100 overlap at 4000–4120 mV). If you want a finer
  display, derive it from the voltage yourself (not something Synapse does).
- Only valid while the link is Active. When the link drops, Synapse shows level 0 / not charging.

### 4.4 Side tone (Mic Monitoring)
- Get `FD 04 00 F1 05 81 60 B4 01`: memory read, 1 byte at `0xB460` on the headset.
- Set `FF 04 0E F1 05 01 04 00 vv` (module `0E`).
- `vv` is a **signed** byte from −64 (`0xC0`, off) to −20 (`0xEC`, maximum): 44 steps. It is probably
  a gain in dB (UNCERTAIN).
```js
// set (Sc):   n = pct / 100 * 44 - 64;  o[0] = n   (Uint8Array → ToUint8: truncate toward zero, mod 256)
// get (A):    level = (data[0] - 256 - -64) / 44 * 100
```
  | UI % | raw | | UI % | raw |
  |---|---|---|---|---|
  | 0 (or switch off) | `C0` (−64) | | 50 | `D6` (−42) |
  | 25 | `CB` (−53) | | 75 | `E1` (−31) |
  | 100 | `EC` (−20) | | | |
  Truncation toward zero means a negative raw value rounds **up**, e.g. 33 % → −49.48 → −49 (`CF`).
- The switch has no separate command. In the BasicFeature runner (`3487` `ci`), for
  `AudioAvneraDevice` + `thxv3` Synapse does:
  1. `thxDevice.setStartCaptureStatus(enabled)`: tells the THX DLL to open (or close) a capture stream
     on the Chat microphone endpoint (see the audio doc).
  2. `rzDevice.setSidetone(enabled ? value : false)`. `false` → 0 % → `C0`.
  Every logged write in this install was `FF 04 0E F1 05 01 04 00 C0` (side tone off).
- UNCERTAIN: whether the headset's side tone works without an open capture stream. Synapse always
  opens one when side tone is enabled; do the same (e.g. open a WASAPI capture client on the Chat
  endpoint and discard the samples).

### 4.5 Power saving (auto power-off) = "Silence Detection Control"
- Get `FD 04 12 F1 02 41` → `en lo hi`; Set `FF 04 12 F1 05 41 en lo hi`.
- `en`: 1 = enabled, 0 = disabled. `period` = **seconds**, uint16 little-endian
  (`o[1] = l7(n) = n & 0xFF; o[2] = E4(n) = n >> 8 & 0xFF`; read `Jz(data[2], data[1]) = hi << 8 | lo`).
- The feature flag `powerSavingUnit = "second"` makes the UI convert minutes to seconds. The UI slider
  is 15–60 min ("Device will turn off after (mins) of inactivity when running on battery").
- Verified: profile `powerSaving {isEnabled: true, value: 15}` → logged
  `FF 04 12 F1 05 41 01 84 03`, and the live read returns `01 84 03` (900 s).

### 4.6 Haptics (HyperSense, "Haptic Intensity")
- Get `FD 04 02 F1 03 20 00` → `en lo hi xx`; Set `FF 04 02 F1 06 20 en lo hi 00`.
- `en`: 1 on, 0 off. `level`: uint16 LE, the UI value sent unchanged. The Synapse slider runs
  **20–100 in steps of 10**, default 50 (logged: off + 50 → `FF 04 02 F1 06 20 00 32 00`). Turning the
  switch off keeps the level (`00 32 00`).
- The 4th read byte was `0x7B` (123) in the live capture. Synapse ignores it (UNCERTAIN; maybe a limit).
- The code writes `o[3] = 0` into a 3-byte array (a no-op), but `len = 06` makes the frame one byte
  longer than the payload. That byte is the zero padding, so the effective frame is
  `FF 04 02 F1 06 20 en lo hi 00`.
- Synapse sends both fields on every change. Toggling the switch re-sends the current level; moving the
  slider re-sends the current enabled flag (`3487` `jt` / `Bt`).
- The Nari Ultimate has no hardware haptic button: its report descriptor lacks the `HAPTIC_BUTTON_EVENTS`
  record (97) that other Razer headsets use.

### 4.7 Lighting (Chroma)
One lighting zone: both earcup logos show the same colour. The firmware has only three modes
(enum `79949.z`):

| `ee` | Name | Set frame |
|---|---|---|
| `00` | Off | `FF 04 12 F1 06 70 00 00 00 00` |
| `01` | Static | `FF 04 12 F1 06 70 01 rr gg bb` |
| `08` | CustomFrame (PC-driven) | `FF 04 12 F1 06 70 08 00 00 00`, then stream colours with Set Chroma Custom Frame |

- `setChromaEffect(effectId, rgb)` copies `rgb` only when `effectId == 1`; any other effect gets
  `00 00 00`.
- **Set Chroma Custom Frame** `FF 04 12 F1 05 72 rr gg bb`: one colour. `setChromaFrame` uses only the
  first three bytes of the colour array, and the lighting engine path sends one frame per row of its
  grid (the Nari's grid is a single LED).
- Get Chroma Effect → `ee r g b`. Live capture `08 18 58 FF` = CustomFrame, last colour `#1858FF`,
  because Synapse was running Spectrum Cycling at the time.
- `setChromaEffectCommon({effectId, colorArray})` maps lighting-engine effects to firmware modes:
  `Off → 00`, `Static → 01`, anything else `→ 08`. The device manifest declares
  `supportHWEffects: []`, so Synapse renders **every** Quick Effect on the PC, Static included, and
  streams it frame by frame (25 fps, §7.4). **If no program streams frames, the LEDs hold the last
  colour.** RazerAudioPC has to animate Breathing / Spectrum / Audio Meter itself.
- What Synapse actually sends:
  - On device-ready and power-on it tries to switch to effect `08`, but the call always throws
    (`this.colorArray is not iterable`: 27 attempts, 27 errors in the lighting-engine logs). In
    practice it streams custom frames **without ever sending effect `08`**. Custom frames are evidently
    accepted in whatever mode the headset is in; the live read showed mode `08`, which some earlier
    write must have set. Send `FF 04 12 F1 06 70 08 00 00 00` once before streaming: it is the
    documented mode and it is harmless.
  - On exit it sets static green `FF 04 12 F1 06 70 01 00 FF 00` (`quickEffectOnExit`, logged 16 times),
    so the headset never goes dark when Synapse quits.
  - It never sends Off (`00`). "Lighting off" is done with brightness 0.
- Brightness: Get `FD 04 12 F1 02 71` → `bb`; Set `FF 04 12 F1 03 71 bb`, `bb` = 0–255.
  ```js
  set: n[0] = 255 * pct / 100          // truncated: 50 % → 127
  get: brightness = Math.round(100 * data[0] / 255)
  ```
  Logged: `Set Chroma Brightness` at 100 % → `FF 04 12 F1 03 71 FF`. Synapse's brightness switch sends
  `0` when off and the slider value when on (`setChromaBrightnessState(enabled ? value : 0)`).

---

## 5. Lifecycle

### 5.1 Headset on/off
- The dongle answers all node-`00` commands at any time.
- Node-`F1` (headset) commands only answer while `linkStatus == 1`. Otherwise no matching reply
  arrives and Synapse reports `FAIL` after its retries.
- If the headset is off when Synapse starts, link status reads `3` (Searching), headset reads fail
  with "command unknown" (about 1.1 s apart), and nothing is sent until the first `0x64 = 1` event.
- Synapse pushes the profile to the headset **only on the first link-up of a session**. Later power
  cycles get just Get Link Status ×2 + Get Battery Status. The headset therefore probably keeps
  haptics, side tone, power saving and brightness across power-off (UNCERTAIN: not proven, the
  headset may not have been power-cycled since the last write). **Recommendation:** re-apply every
  hardware setting on each `0x64 = 1` event; the writes are cheap. Lighting frames have to restart
  anyway.

### 5.2 Startup sequence used by Synapse (from the logs, headset on)
1. Open HID interface 5 (`connectHidDevice`, 3 tries, probe = Get Extended Firmware Version).
2. Get Link Status (repeated); while it is `6` (InitialScan) or fails, wait 1 s and retry.
3. Get Battery Status, Get Serial Number, Get Edition Information, Get Extended Firmware Version
   (Get Edition is repeated 2–3 times; that's harmless).
4. Profile replay. The HID writes go out in this order:
   - Get Silence Detection Control
   - Set Chroma Brightness
   - Set Haptics Control
   - Set Side Tone (after `setStartCaptureStatus`)
   - Set Silence Detection Control (re-sent once more because of a typo in Synapse's change check)

   THX/APO settings are applied in parallel ([NARI_ULTIMATE_AUDIO.md](NARI_ULTIMATE_AUDIO.md)), and
   the lighting engine starts streaming custom frames.
5. Polling: `CHECK_BATTERY_STATUS` every **15 s** (10 s while charging). Each tick is Get Link Status
   (up to 2 tries 400 ms apart) and then Get Battery Status. Synapse shows a low-battery toast at
   ≤ 10 % when not charging. There is no keep-alive command.
6. On exit: static green (§4.7). No other HID writes.

---

## 6. Events (input reports on `COL02`)

`rzHardwareEventsAudioAvneraParser`: `recordId = report[0]` (the report id), values from `report[1]`.

| Report id | Record (`89846.VO`) | Parsing | Synapse reaction |
|---|---|---|---|
| `0x64` (100) | `AUDIO_AVNERA_LINK_STATUS_EVENTS` | `eventId = report[1]` (link status enum, §4.1) | `1` → re-read serial if missing, then battery (`CHECK_BATTERY_STATUS`). Anything else → show level 0, not charging |
| `0x65` (101) | `AUDIO_AVNERA_MIC_STATUS_EVENTS` | parser: `eventId = (report[1] & 0x04) >> 2` (1 = MUTED, 0 = UNMUTED); handler: mic enabled = `report[1] == 0` | mirrors the headset's hardware mic mute onto the Windows capture endpoint (`isUseSystemMute = true`, `ON_SET_MIC_VOLUME` with `isEnabled = !muted`) and updates the UI |

- Report `0x65` declares three 1-bit fields (usages `0xFFC0:0x15`–`0x17`). Synapse uses only bit 2.
  The meaning of bits 0–1 is UNCERTAIN.
- The mic mute is done in the headset (mute button / boom). The event exists so software can show the
  state and keep the Windows endpoint in sync. It can't be polled over HID; read the event, or read the
  capture endpoint's mute state from Windows.
- Volume wheel and media keys arrive on `COL01` as standard consumer-control usages; Windows handles them.

---

## 7. Implementing it (RazerAudioPC)

### 7.1 Opening the device (Win32, as verified)
1. Enumerate `GUID_DEVINTERFACE_HID` with `SetupDiGetClassDevs(…, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE)`.
   Keep paths containing `vid_1532&pid_051a&mi_05`.
2. For each path, `CreateFile(path, GENERIC_READ|GENERIC_WRITE, FILE_SHARE_READ|FILE_SHARE_WRITE, …, OPEN_EXISTING)`,
   then `HidD_GetPreparsedData` + `HidP_GetCaps`:
   - `UsagePage == 0xFF00 && FeatureReportByteLength == 64` → command channel (`COL03`);
   - `UsagePage == 0xFFC0` → event channel (`COL02`, open for overlapped reads, 2-byte input reports).
3. (WinRT alternative: `HidDevice.GetDeviceSelector(0xFF00, 0x0001, 0x1532, 0x051A)` with
   `SendFeatureReportAsync` / `GetFeatureReportAsync(0xFF)`; vendor usage pages are allowed.)

### 7.2 One transaction
```csharp
// cmd = request frame without the FF 0A 00 prefix; expect = reply header (op..len + echoed id bytes)
static byte[]? Transact(SafeFileHandle h, byte[] cmd, byte[]? expect)
{
    var buf = new byte[64];
    buf[0] = 0xFF; buf[1] = 0x0A;                     // buf[2] = 0
    cmd.CopyTo(buf, 3);
    if (!HidD_SetFeature(h, buf, buf.Length)) throw new IOException("SetFeature");
    if (expect is null) return null;                  // set commands: no reply is read
    for (int i = 0; i < 10; i++)
    {
        Thread.Sleep(100);
        var r = new byte[64]; r[0] = 0xFF;
        if (!HidD_GetFeature(h, r, r.Length)) continue;
        if (!r.AsSpan(3, expect.Length).SequenceEqual(expect)) continue;   // not our reply yet
        int count = expect[4] - (expect.Length - 4);
        return r.AsSpan(3 + expect.Length, count).ToArray();
    }
    return null;                                      // timeout → link probably not Active
}
```
- Hold a lock around each transaction. Synapse waits 100 ms before the first `GetFeature`. Live tests
  answered on the first poll; a shorter first wait (10–20 ms) may also work (UNCERTAIN).
- For multi-frame reads (serial, edition), send every frame with `SetFeature`, then poll for the reply
  header of the last one.

### 7.3 Coexisting with Synapse
Both programs can open the device at once, and a reply is only accepted when its header matches. A
running Synapse streams lighting frames continuously (overriding any lighting you set). It re-applies
its profile at startup, on profile switches (including per-app profile switching) and whenever its UI
changes a setting. Tell the user to quit Synapse (or uninstall it) before using RazerAudioPC with the
Nari.

### 7.4 Lighting animations Synapse renders on the PC
Set the mode to CustomFrame (`ee = 08`) once, then send `FF 04 12 F1 05 72 r g b` on a timer.
- **Rate:** Synapse's native lighting engine renders at 25 fps (`fps: 25`). JS reads engine events
  every 40 ms and writes one Set Chroma Custom Frame per rendered frame, skipping writes while
  brightness is 0. Each write is one 64-byte control transfer on the same channel as the other
  commands, so take the lock per frame. Skip frames whose colour hasn't changed.
- **Topology:** device manifest `ledConfig` is a 1×1 matrix, `LedRegion [{RegionId 4, NumLed 1}]`.
  There is one colour for the whole headset.
- **Effects:** Static, Breathing, Spectrum Cycling and Audio Meter, with Synapse's exact timings and
  colour stops, are in [NARI_ULTIMATE.md](NARI_ULTIMATE.md) §3.
- **Display off:** "Switch off lighting when display is turned off" sends Set Chroma Brightness `00` on
  `displaypoweroff` and restores the stored brightness on `displaypoweron`. Dimming is ignored. Pause
  frame streaming while off.

---

## 8. Verification log

Sent by a read-only probe (only `FD` frames) to the headset on 2026-10-29, Synapse running,
headset on, Synapse profile "Audio" active:

| Command | Raw reply (report bytes 0…) | Decoded | Synapse UI |
|---|---|---|---|
| Get Link Status | `FF 0C 05 FE 00 04 0F 05 81 AA C6 01` | Active | connected |
| Get Battery Status | `FF 0F 05 FE 12 04 1F 08 05 03 05 01 0F 30 50` | on battery, 3888 mV, 80 % | 80 % |
| Get Extended FW (headset) | `FF 1B 05 FE 07 04 1F 14 02 30 31 30 36 2C 63 6C 69 65 6E 74 2C 32 30 31 38 2D 31` | `0106,client,2018-1` → 0.1.0.6 | 0.1.0.6 |
| Get Dock Extended FW | `… FE 07 04 0F 14 02 30 31 30 36 2C 68 6F 73 74 2C 32 30 31 38 2D 31 30 2D` | `0106,host,2018-10-` | – |
| Get Haptics Control | `FF 0D 05 FE 02 04 1F 06 20 00 32 00 7B` | off, 50 | switch off, 50 |
| Get Silence Detection | `FF 0C 05 FE 12 04 1F 05 41 01 84 03` | on, 900 s | on, 15 min |
| Get Chroma Effect | `FF 0D 05 FE 12 04 1F 06 70 08 18 58 FF` | CustomFrame, `#1858FF` | Spectrum Cycling |
| Get Chroma Brightness | `FF 0A 05 FE 12 04 1F 03 71 FF` | 255 = 100 % | on, 100 |
| Get Side Tone | `FF 0C 05 FE 00 04 1F 05 81 60 B4 C0` | −64 = 0 % | switch off |

Set commands were **not** sent during verification. Their encodings come from the code and match
Synapse's logged writes: Set Haptics `FF 04 02 F1 06 20 00 32 00`, Set Side Tone
`FF 04 0E F1 05 01 04 00 C0`, Set Silence Detection `FF 04 12 F1 05 41 01 84 03`, Set Chroma Brightness
`FF 04 12 F1 03 71 FF`. Set Chroma Effect and Set Chroma Custom Frame are not logged by Synapse;
they come from code only.
