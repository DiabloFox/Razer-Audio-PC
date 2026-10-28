# Razer Hammerhead Pro HyperSpeed — BLE control protocol

Reverse-engineered from the Razer Audio Android app (`com.razer.audiocompanion`, v18.0.0, jadx 1.5.1).
Device class: `com.razer.audiocompanion.model.devices.HammerheadT1V2` (`name = "Hammerhead Pro HyperSpeed"`,
internal codename family "Amelia T1 v2 HS", `device_key = "hammerhead_tws_t1v2"`, `firmwarePath = "hammer_t1v2"`,
`device_id = C.DEVICE_ID_HAMMERHEAD_T1_V2 = 9000021`).

Inheritance chain: `HammerheadT1V2 → AudioTws → AudioV2 → AudioDevice`.
Every command below is the one that actually applies to T1V2 after walking that chain (the source class is noted).
`HammerheadT1V2.updateFromDevice` does **not** call `super.updateFromDevice`, so `AudioTws.updateFromDevice` /
`AudioDevice.updateFromDevice` are dead for this device.

Byte notation: `0x93 (-109)` = hex value and the signed Java `byte` literal that appears in the source.
"UNCERTAIN" marks anything inferred and not proven by the code.

---

## 1. Transport

### 1.1 GATT layout (from `HammerheadT1V2()` constructor)

| Item | Value |
|---|---|
| Primary service | `0000fd65-0000-1000-8000-00805f9b34fb` (16-bit `0xFD65`) |
| Scan service filter (`scanningService`) | `0000fd65-0000-1000-8000-00805f9b34fb` |
| Write characteristic (`writeUuid`) | `416d0000-2d52-617A-6572-424c4501f40a` |
| Notify characteristic (`readUuid`, used as `notifyUUid`) | `416d0001-2d52-617a-6572-424c4501f40a` |
| CCCD | `00002902-0000-1000-8000-00805f9b34fb` (`AudioDevice.NOTIFY_DESCRIPTOR`) |
| MTU requested | **240** (`AudioTws.maxMtu = 240`; `lateMtuChange = false` so it is put in `RazerBleConfig` and requested immediately after the link comes up) |
| GATT transport | LE (`connectGatt(ctx, false, cb, 2)` → `TRANSPORT_LE`) |
| Connection timeout | 12000 ms (`connectionTimeout`), connection retries 3 (`retries`) |

`RazerDeviceManager.getAdapterByAudioDevice`:
```java
builder.setMaxMtu(audioDevice.maxMtu);                       // 240
builder.setServiceUUID(UUID.fromString(audioDevice.getServiceUUID()));
builder.setNotifyUUid(UUID.fromString(audioDevice.getReadUuid()));   // 416d0001...
builder.setWriteUUid(UUID.fromString(audioDevice.getWriteUuid()));   // 416d0000...
builder.setTimeout(audioDevice.connectionTimeout);
builder.setRetries(audioDevice.retries);
```
The "read" characteristic is never read for this device; it is only the notification source.

### 1.2 Connection sequence (`RazerBleAdapter`)

1. `connectGatt(..., TRANSPORT_LE)`.
2. On `STATE_CONNECTED` → `requestMtu(240)`.
3. `onMtuChanged` → sleep 1000 ms → `discoverServices()`.
4. `onServicesDiscovered` → (bonding mode: posted with 500 ms delay) `initGatt`:
   - if service `0xFD65` is missing → fail ("KNOWN BUG on firmware ... restart device").
   - sleep 400 ms.
   - `setCharacteristicNotification(notifyChar, true)` and write CCCD = `ENABLE_NOTIFICATION_VALUE` = `01 00`
     (notifications, not indications). Up to 3 attempts; on failure disconnects.
   - connection considered successful once the descriptor write was issued.
5. No handshake, no authentication, no session key. The first application command is GET GRS (`0x32`).

Pairing/bonding: the app creates a **classic** BT bond to the "classic" address (see §4.3) for audio; the BLE control
channel itself does not perform any app-level auth. Whether the firmware requires an encrypted (bonded) LE link for the
characteristics is UNCERTAIN (the app runs with `withBonding = true` on the adapter, which only adds the 500 ms delay).

### 1.3 Write type

`sendSynchronizeCommandByDevice` (used by every `sendSchronizedCommandByAddress` call) does **not** call
`setWriteType`, so Android uses the characteristic default: Write-Without-Response if the characteristic has
`PROPERTY_WRITE_NO_RESPONSE`, otherwise Write-With-Response. The chunked / unsynchronized paths set it explicitly
(`z10 ? WRITE_TYPE_DEFAULT(2) : WRITE_TYPE_NO_RESPONSE(1)`), and the chroma "frame pool" uses no-response — none of those
paths are used by T1V2. **Recommendation for WinRT:** inspect `GattCharacteristicProperties` of `416d0000…`; use
`WriteWithoutResponse` if present, else `WriteWithResponse`. (Exact property set: UNCERTAIN.)

### 1.4 Framing — there is none beyond the command bytes

`sendSchronizedCommandByAddress(address, bytes, timeoutMs, retries, label)`:
```java
// retry wrapper
while (i11 < i10) { try { return sendSchronizedCommandByAddress(str, bArr, j3); }
                    catch (BleDeviceTimeoutException | InterruptedException e10) { i11++; if (i11 >= i10) throw e10; } }
// single attempt
return sendSynchronizeCommandByDevice(gatt.getDevice(), bArr, bArr[0], j3);
// ...
characteristic.setValue(bArr);                 // raw bytes, nothing prepended/appended
bluetoothGatt.writeCharacteristic(characteristic);
ResponseLatch responseLatch = new ResponseLatch();
this.responseLatchHashMap.put(Byte.valueOf(b10), responseLatch);   // b10 = bArr[0]
if (responseLatch.await(j3, TimeUnit.MILLISECONDS)) return responseLatch.getResponse();
throw new BleDeviceTimeoutException(...);
```
- The bytes written are exactly the command array (e.g. `13 00 00`). **No** header, transaction id, CRC, or
  sequence number is added. (`getTransactionID()` is only used for log lines.)
- Retries: only on timeout; the same bytes are re-written. Retry count is per call (see the per-command table, §3).

### 1.5 Response matching

`CustomBleCallback.onCharacteristicChanged`:
```java
ResponseLatch responseLatch = responseLatchHashMap.get(Byte.valueOf(value[0]));
if (responseLatch != null) { responseLatchHashMap.clear(); responseLatch.returnResponse(value); }
else { for (ResponseLatch l : sendAndWaitFor2ndNotification) l.returnResponse(value); sendAndWaitFor2ndNotification.clear(); }
sendCharacteristicNotiy(gatt, characteristic, value);   // every notification is also broadcast to UI listeners
```
- A notification completes the pending request whose **command byte equals `notification[0]` exactly**
  (no masking: a SET `0x93` is answered by a notification starting with `0x93`, a GET `0x13` by `0x13`).
- The pending-request map is cleared on any match (effectively one outstanding request at a time).
- Every notification (matched or not) is also delivered to all registered `RazerBleDataListener`s — this is how
  unsolicited events (§3.17) are handled.
- **No multi-notification reassembly**: one notification = one complete response. With MTU 240 a payload can be up to
  237 bytes; the largest T1V2 responses (touch map: 45 bytes, quick-connect host list) fit in one notification.
  (If your Windows link negotiates a smaller MTU, long responses may be truncated — request a large MTU.
  UNCERTAIN whether firmware fragments in that case.)
- Caveat: an unsolicited notification whose byte0 equals a pending command byte would be taken as the response; the
  app accepts that risk. You may want to require `resp[1] != 0x02` for solicited responses.

---

## 2. Packet format

### 2.1 Request
```
[0] command id      GET = 0x00..0x7F, SET = GET | 0x80 (e.g. GET EQ 0x13, SET EQ 0x93)
[1] 0x00            always 0 in requests
[2] payload length  N
[3..3+N-1] payload
```
Exception (copied verbatim from source, `AudioV2.createSetAutoPauseCommand`): `{-94, 0, 0, v}` = `A2 00 00 vv` —
length byte is 0 although one payload byte follows. Reproduce it as-is (that is what the firmware receives from the
official app).

### 2.2 Response / notification
```
[0] command id (echo of request byte0)
[1] status / packet type: 0x00 = OK response; 0x02 = unsolicited event notification
[2] payload length N
[3..] payload   (for SET commands resp[3] is usually a result code: 0 = success)
```
Evidence: `sendSynchronizedCommandPenny` treats `resp[1] != 0` as `COMMAND_ERROR`; all event handlers test
`bArr[1] == 2` (e.g. `QuickConnectHost.extractFromNotify`: `bArr[0] == 45 && bArr[1] == 2`). Meaning of other
non-zero `resp[1]` values: UNCERTAIN (treat as error). `Commands.createGetGrsResponsePacket` builds `{0x32, 1, 1, b}`
(a mock with byte1=1), so 0x01 may also appear — treat as "not OK" / UNCERTAIN.

Success checks used by the app for SET commands: EQ bands `resp[3] == 0`; touch map `resp[3] == 0`; chroma effect
`resp[3] == 0`; fit test start/cancel and tutorial start/stop `resp[3] > 0`. All other SETs ignore the response
content (only "a notification with the same byte0 arrived").

---

## 3. Commands (Hammerhead Pro HyperSpeed)

### 3.0 Summary table

| Feature | GET | SET | Timeout ms × tries (app) | Target bud |
|---|---|---|---|---|
| GRS (role/side) | `32 00 00` | – | 1000 × 4 | each |
| Firmware version (+language) | `02 00 00` | – | 400 × 5 (then 400 × 2) | primary |
| Battery | `21 00 00` | – | 500 × 3 | primary |
| ANC / Ambient | `12 00 00` | `92 00 01 vv` | 500×2 / 300×3 | primary |
| EQ preset | `13 00 00` | `93 00 01 vv` | 500×2 / 150×2 | primary |
| EQ custom bands | `15 00 00` | `95 00 0A b0..b9` | 200×2 / 300×3 | primary |
| Auto pause | `22 00 00` | `A2 00 00 vv` (sic) | 500×2 / 200×2 | primary |
| Touch mapping | `23 00 00` | `A3 00 2A …42 bytes` | 450 × 1 (1000 on Razer phones) | **each bud separately** |
| Do Not Disturb (auto-switch) | `27 00 00` | `A7 00 01 vv` | 100×3 / 1000×3 | primary |
| Gaming mode / Quick-connect mode | `2B 00 00` | `AB 00 01 vv` | 200×2 / 200×2 | primary |
| Auto power-off (timeout) | `2C 00 00` | `AC 00 01 mm` | 500×2 / 600×2 | primary (not shown in UI) |
| Quick-connect host list | `2D 00 00` | – | 500 × 2 | primary |
| Quick-connect to host | – | `AD 00 06 m0..m5` | 1000 × 2 | primary |
| Low power mode | `2F 00 00` | `AF 00 01 vv` | 500×3 / 300×2 | primary (fw ≥ 01.04.04.03) |
| Chroma effect | `40 00 00` | `C0 00 LL ee …` | 300×3 / 3000×2 | primary |
| Chroma brightness | `41 00 00` | `C1 00 01 bb` | 300×2 / 300×3 | primary |
| Wear status | `50 00 00` | – | 500 × 2 | primary |
| Fit status | `51 00 00` | – | 500 × 2 | primary |
| Fit test start / cancel | – | `D0 00 00` / `D1 00 00` | 1000 × 2 | primary |
| Gesture tutorial start / stop | – | `E0 00 00` / `E1 00 00` | 450 × 1 | primary |
| Gaming mode (legacy cmd) | `14 00 00` | `94 00 01 vv` | – | not used for T1V2 (see 3.8) |

Signed decimals: `0x92=-110, 0x93=-109, 0x94=-108, 0x95=-107, 0xA2=-94, 0xA3=-93, 0xA7=-89, 0xAB=-85, 0xAC=-84,
0xAD=-83, 0xAF=-81, 0xC0=-64, 0xC1=-63, 0xD0=-48, 0xD1=-47, 0xE0=-32, 0xE1=-31`.
Decimal GET ids: `0x02=2, 0x12=18, 0x13=19, 0x14=20, 0x15=21, 0x21=33, 0x22=34, 0x23=35, 0x27=39, 0x2A=42, 0x2B=43,
0x2C=44, 0x2D=45, 0x2F=47, 0x32=50, 0x40=64, 0x41=65, 0x50=80, 0x51=81, 0x62=98`.

### 3.1 GRS — side / primary / standalone (`AudioTws`)
- GET `32 00 00` (`Commands.GET_GRS_STATUS = 50`), response `32 00 01 gg`; value = `resp[3]`.
- The same GRS byte is the **last byte of the advertisement manufacturer data** (`HammerheadT1V2.setScanData →
  setGrs(bArr[bArr.length - 1])`).
```java
this.isLeft       = ((byte)(((byte)(b10 << 7)) >> 7)) == 0;   // bit0 == 0  → LEFT bud,     1 → right
this.isStandalone = ((byte)(((byte)(b10 << 6)) >> 7)) == 0;   // bit1 == 0  → standalone,   1 → paired TWS
this.isPrimary    = ((byte)(((byte)(b10 << 5)) >> 7)) == 0;   // bit2 == 0  → PRIMARY bud,  1 → secondary
```

### 3.2 Firmware version / language (`AudioDevice.updateFirmwareVersion`, `updateLanguageValueAndCompleteFirmwares`)
- GET `02 00 00`.
- `device_firmware_version = resp[3..6]` (4 ints, signed bytes). Printed as `"%d.%d.%d.%d"`; padded form used for
  version gates is two-digit zero-padded `"01.02.02.00"`.
- `languageValue = resp[resp.length - 1]` (last byte) → voice-prompt language (§3.15).
- On connect the primary also runs `updateLanguageValueAndCompleteFirmwares`: bytes from index 3 are split into
  4-byte groups → `complete_firmwares[0..5][4]` (unsigned). Group order presumably follows `Firmware.TYPE`
  = `MC, DC, D2, DP, CT, AD` (component images). Group 0 = main version. Exact component layout: UNCERTAIN.
  `AudioTws.firmwareVersionLength = 20`.

### 3.3 Battery (`AudioV2.createGetBatteryCommand`, `BatteryPresenter`)
- GET `21 00 00` → `21 00 02 xx yy`. Values are percent, unsigned (`& 0xFF`). No case battery is reported.
- Which byte is which:
  - `BatteryPresenter.updateBattery` has a **T1V2-specific** branch: `list[0] = batteries[4]` → shown as LEFT,
    `list[1] = batteries[3]` → shown as RIGHT. `Battery.getBatteryLevels` (used by the Amelia flashers) also does
    `leftBattery = [4]; rightBattery = [3]`.
  - The generic notification handler instead does `if (next.isLeft) { self = [3]; other = [4]; } else { self = [4]; other = [3]; }`.
  - Conclusion: **`resp[3]` = right bud, `resp[4]` = left bud** (fixed order) is the most consistent reading for T1V2;
    the generic "byte3 = connected bud" interpretation conflicts. UNCERTAIN — verify on hardware (take one bud out).
  - `AudioDevice.updateBatteries` (single value) uses `[isLeft ? 3 : 4]`, falling back to the other index if ≥ 255.
    A value of 0xFF probably means "bud not available". UNCERTAIN.
- Charging event: unsolicited notification `2A 02 ll cc` (`batteryChargingStatus = 42`), `resp[3] == 1` → charging,
  otherwise not charging. The app then refreshes the battery display.
- Battery notifications `21 02 02 xx yy` may also arrive unsolicited (handled with the same parser).

### 3.4 ANC / Ambient (`AudioV2.createGetAncCommand`, `HammerheadT1V2.setANCValue`)
- GET `12 00 00` → value `resp[3]` (signed in source; `-1` = 0xFF).
- SET `92 00 01 vv` (300 ms × 3). App stores `ancValue = vv`, does not re-read.
- Values used by the app for T1V2 (`newAncDialog = true`, `showANCLevel() = true`, `isDifferentAmbientValue = true`):

| Value | Meaning | Enum used |
|---|---|---|
| `0x00` | ANC off | `MABEL_ANC_OFF` / `AMELIA_ANC_0FF` |
| `0x01`…`0x0A` | ANC **on**, level 1…10 (slider "ANC Level", min 1, max 10, step 1) | `MABEL_ANC_0N_ONE`…`MABEL_ANC_0N_TEN` |
| `0xFF` (-1) | Ambient (transparency) | `MABEL_ANC_AMBIENT` / `AMELIA_ANC_AMBIENT_FF` (value 255) |

  `supportedAncSettings = [AMELIA_ANC_0N(1), AMELIA_ANC_0FF(0), AMELIA_ANC_AMBIENT_FF(255)]` (list order On, Off, Ambient).
  Selecting "On" without a remembered level sends level **10** for T1V2 (`if (!(primary instanceof HammerheadT1V2)) setAnc(ONE) else setAnc(TEN)`);
  otherwise it re-sends the last slider level.
- Unsolicited: `12 02 01 vv` when the user toggles ANC with a gesture → `ancValue = data[3]`.
- UI gating: ANC "On"/"Ambient" items are disabled unless at least one bud is worn (wear status §3.13);
  toast "Put on both earbuds and ensure a comfortable fit." Setting ANC > 0 posts `ForcePowerModeOff` (UI turns Low
  Power Mode off).
- `forceEnableAncSwitching()` (T1V2: `autoPauseValue == 0 && fw >= "01.02.00.00"`) exists but has no call sites in
  this app version.

### 3.5 EQ presets (`AudioV2.createGetEqCommand / createSetEqCommand`, `HammerheadT1V2.setEqValue`)
- GET `13 00 00` → `eqValue = resp[3] & 0xFF`.
- SET `93 00 01 vv` (150 ms × 2); app sets `eqValue = vv` without re-reading.
- `supportedEQ` (display order) and values:

| Enum | Byte | Label | Reference curve (`EqConstants`, display units) |
|---|---|---|---|
| `AMELIA_T1_V2_DEFAULT_THX` | `0x00` | "Default (THX)" | `THX = {0,0,0,0,0,0,0,0,0,0}` |
| `AMELIA_T1_V2_AMPLIFIED` | `0x01` | "Amplified" | `AMPLIFIED = {-2,2,3,4,5,5,5,4,2,-2}` |
| `AMELIA_T1_V2_VOCAL` | `0x02` | "Vocal" | `VOCAL = {3,3,2,1,-2,-2,-2,1,2,4}` |
| `AMELIA_T1_V2_ENHANCED_BASS` | `0x03` | "Enhanced Bass" | `BASS = {5,5,5,2,0,0,0,2,2,2}` |
| `AMELIA_T1_V2_ENHANCED_CLARITY` | `0x04` | "Enhanced Clarity" | `ENHANCED_CLARITY = {-5,-4,-2,-1,0,0,0,2,2,2}` |
| `AMELIA_T1_V2_CUSTOM` | `0xFF` (255) | "Custom" | user bands (§3.6) |

  The preset curves are app-side reference data only; they are never transmitted (presets are applied by firmware).
  `catValue = -1` for all → no EQ-group command is sent.

### 3.6 Custom 10-band EQ
- Bands (`AudioEQ.bandNames`): **31, 63, 125, 250, 500, 1K, 2K, 4K, 8K, 16K** Hz.
- Encoding: one unsigned byte per band, **0…10, 5 = flat (0)**. (`bandOffset()=5`, `bandRange()=10`,
  `minBand=0`, `maxBand=10`; UI: `raw = 5 + level`, clamped to [0,10]; displayed level = raw − 5 → −5…+5.)
  dB per step: UNCERTAIN (likely 1 step = 1 dB… not stated anywhere).
- GET bands: `15 00 00` (T1V2 uses `AudioDevice.getEqBands()` = `{21,0,0}`) → `15 00 0A b0..b9`;
  `bandValues = resp[3 .. 3+resp[2]-1]`.
- SET bands: `95 00 0A b0 b1 … b9` (`AudioDevice.createSetEqBands`: `[-107, 0, len, bands…]`), 300 ms × 3,
  success iff `resp[3] == 0`.
- App flow when the user picks Custom: `SET EQ 93 00 01 FF` → `GET bands 15 00 00` → show sliders.
  While dragging, the app batches: at most one `95` write per 500 ms, duplicates within 1 s are dropped; it always sends
  all 10 bands. "Reset" sends all 5s (`currentBands = {5,5,5,5,5,5,5,5,5,5}`).
  UNCERTAIN whether writing `95` while a non-custom preset is active switches the device to custom; the app only
  writes bands while Custom is selected.

### 3.7 Auto Pause / Resume (`AudioV2`)
- GET `22 00 00` → `autoPauseValue = resp[3]` (1 = on, 0 = off).
- SET `A2 00 00 vv` (exactly as in source, length byte 0), then the app re-reads with GET.
- `AutoPauseSettings`: `ON = 1`, `OFF = 0`. Label "Auto Pause / Resume", description "Auto Pause when off ears".

### 3.8 Gaming mode & Quick Connect mode (`QUICKSettings`, `isNewQuickSetting = true`)
For T1V2 the Game Mode switch is implemented through the **quick-settings** command, not `0x14/0x94`
(`HammerheadT1V2` sets `supportedGamingModeSettings = null`, so `GamingModeSettingsPresenter` is inert).
- GET `2B 00 00` → `quickSettingsValue = resp[3]`.
- SET `AB 00 01 vv`. App sets `gamingModeValue = (vv == 1) ? 1 : 0`.

| `QUICKSettings` | Byte | Meaning |
|---|---|---|
| `OFF` | `0x00` | neither (not offered in T1V2 UI) |
| `GAMING_MODE_ON_QC_OFF` | `0x01` | Game Mode (low latency) **on**, Quick Connect off |
| `QC_ON_GAMING_MODE_OFF` | `0x02` | Game Mode **off**, Quick Connect enabled |

  Dashboard switch "Game Mode": ON → `AB 00 01 01`, OFF → `AB 00 01 02`.
- Unsolicited: `2B 02 01 vv` when changed on the buds (app ignores it for 50 ms after its own write); `vv == 1` → on.
- Legacy commands still defined for the class (not used by the T1V2 UI): GET `14 00 00`, SET `94 00 01 vv`
  (`GamingModeSettings`: `OFF = 0`, `ON = 1`).

### 3.9 Quick Connect (multipoint host switching)
- GET host history `2D 00 00` → `2D 00 LL <records…>`; if `resp[2] == 0` → empty list. Each record:
  ```
  [recLen] [connected] [mac0..mac5] [name UTF-8 × (recLen - 7)]
  ```
  `connected > 0` → currently connected host. (`i11 = i13 - 7` where `i13 = recLen`.)
- Connect to host: `AD 00 06 m0 m1 m2 m3 m4 m5` (`createSetQuickConnectDevice`), 1000 ms × 2; MAC bytes exactly as
  received in the record.
- Unsolicited `2D 02 LL ee m0..m5 name… xx`: `ee` = event (`QuickConnectId`: `0 DELETE_PAIR`, `1 NEW_PAIR`,
  `2 CONNECTED`, `3 DISCONNECTED`), MAC = `data[4..9]`, name = `data[10 .. len-2]` (last byte excluded).
- UI: item "Quick Connect" shows the connected host name (or "No known audio source"); disabled when the list is
  empty or Game Mode is on.

### 3.10 Do Not Disturb (`Features.DND`, implemented with the auto-switch command)
- GET `27 00 00` → `autoSwitchValue = resp[3]` (100 ms × 3).
- SET `A7 00 01 vv` (1000 ms × 3), `vv` = 1 on / 0 off (`AutoSwitchSettings ON=1, OFF=0`).
- Label "Do Not Disturb", description "Block calls while gaming in 2.4 GHz".

### 3.11 Auto power-off / standby (`HammerheadT1V2.createGet/SetTimeoutCommand`)
- GET `2C 00 00` → `timeoutSettingsValue = resp[3]` (read during init).
- SET `AC 00 01 mm`, then re-read.
- `TimeoutSettings` entries for this device (value = minutes):
  `HAMMERHEAD_T1V2_NEVER = 0`, `_5MIN = 5`, `_15MIN = 15`, `_30MIN = 30`, `_45MIN = 45`, `_60MIN = 60`
  (labels "Never", "After 5 minutes" … "After 1 hour"; feature title "Auto Standby").
- **Not exposed by the app**: `supportedTimeout` is never assigned for T1V2 and `Features.TIMEOUT_SETTINGS` is not in
  its feature list, so the dashboard never shows it. The firmware answers the GET (it is mandatory in init:
  failure aborts `updateFromDevice`). Using SET is therefore UNCERTAIN/untested by Razer's UI.

### 3.12 Touch / gesture remapping (`TouchFunction`, `AudioTws.updateTouchFunction`)
Each bud stores its own map → send GET/SET to **that bud's own BLE connection** (`RazerDeviceManager.getTws(isLeft)`).
- GET `23 00 00` → `23 00 LL …`. Parser chosen by `resp[2]`: `25` → V1, `36` → V2, **`42` → V3 (T1V2)**.
- SET `A3 00 2A` + 42 bytes (V3 layout, chosen when A7…D7 non-null). App then re-reads with GET. Success `resp[3]==0`.
- V3 payload layout (index = offset in payload, `resp[3+index]`): rows × gesture columns:

  | Row | Type | Context (inferred from which functions the app writes there — UNCERTAIN naming) | Payload idx |
  |---|---|---|---|
  | A | CallFunction | incoming call ringing (gets ANSWER / REJECT) | 0–6 |
  | B | CallFunction | call active (gets END_CALL) | 7–13 |
  | C | CallFunction | 2nd incoming call while in a call (ANSWER_AND_SWITCH / REJECT) | 14–20 |
  | D | CallFunction | two calls / held call (SWITCH_CALL, END_CALL) | 21–27 |
  | E | StreamFunction | media streaming | 28–34 |
  | F | StreamFunction | idle / not streaming (volume is never put here) | 35–41 |

  Columns 1…7 (`X1`…`X7`):

  | Col | Gesture | `TapEvent` | Label |
  |---|---|---|---|
  | 1 | single tap | `SINGLETAP` | "Single Press" |
  | 2 | double tap | `DOUBLETAP` | "Double Tap" |
  | 3 | triple tap | `TRIPLETAP` | "Triple Tap" (**disabled for T1V2**: `isGestureEnabled` returns false) |
  | 4 | triple tap + hold 2 s | `TRIPLEHOLD` | "TRIPLE TAP HOLD 2 SEC." |
  | 5 | hold 2 s | `HOLD2SEC` | "HOLD 2 SEC." |
  | 6 | hold 4 s (pairing) | – (not in `supportedTouchGestures`) | not editable |
  | 7 | double tap + hold | `DOUBLEHOLD` | "DOUBLE TAP AND HOLD" |

  So the byte order is `A1..A7 B1..B7 C1..C7 D1..D7 E1..E7 F1..F7`.

- `CallFunction` values: `ANSWER 0x00`, `REJECT 0x01`, `END_CALL 0x02`, `ANSWER_AND_SWITCH_TO_NEW_CALL 0x03`,
  `SWITCH_CALL 0x04`, `VOLUME_UP 0x05`, `VOLUME_DOWN 0x06`, `DISABLE 0xFE (-2)`, `NOT_AVAILABLE 0xFF (-1)`.
- `StreamFunction` values: `MUSIC_PLAY_PAUSE 0x00`, `MUSIC_STOP 0x01`, `ENABLE_DISABLE_VOICE_ASSISTANT 0x02`,
  `MUSIC_NEXT_TRACK 0x03`, `MUSIC_PREVIOUS_TRACK 0x04`, `ANC_ON_OFF_TRANSPARENCY 0x05`,
  `ENABLE_DISABLE_GAMING_MODE 0x07`, `PAIRING 0x08`, `VOLUME_UP 0x09`, `VOLUME_DOWN 0x0A`, `SMART_SWITCH 0x0B`,
  `LOW_POWER 0x0C`, `DISABLE 0xFE (-2)`, `NOT_AVAILABLE 0xFF (-1)`.
- Labels: Play/Pause, Stop, Voice Assistant, Next Track, Previous Track, ANC, Game Mode, Bluetooth Pair, Volume UP,
  Volume Down, Smart Switch, Low Power Mode, "DO NOTHING" (DISABLE); calls: "Pick up/Hang up" (ANSWER/END_CALL),
  "Reject Call", "Answer/Switch to new call", Volume UP/Down.
- How the app writes one UI choice into the table:
  - **Common (stream) gesture col N** (`setCommonSingleTap/DoubleTap/TrippleTap/TrippleHold2Sec/Hold2Sec/DoubleHold2Sec`):
    `EN = FN = f`; if `f` is `VOLUME_UP` or `VOLUME_DOWN` → `EN = f`, `FN = DISABLE`. (Hold4Sec col 6: `E6 = F6 = f`.)
  - **Call gesture col N** (`setCall*`):
    - `ANSWER` or `END_CALL` (one UI item) → `AN=ANSWER, BN=END_CALL, CN=DISABLE, DN=DISABLE`
    - `ANSWER_AND_SWITCH_TO_NEW_CALL` → `AN=DISABLE, BN=DISABLE, CN=ANSWER_AND_SWITCH, DN=SWITCH_CALL`
    - `REJECT` → `AN=REJECT, BN=DISABLE, CN=REJECT, DN=DISABLE`
    - `DISABLE` / `VOLUME_UP` / `VOLUME_DOWN` → all four rows = that value
  - Reading back for display: stream = `EN`; call = first non-DISABLE of `AN, BN, CN, DN`.
- Options offered per gesture (T1V2 `initTouchMappingValues`):
  - Stream, cols 1/2/3/4/5: Play/Pause, Next, Previous, Stop, Voice Assistant, Game Mode, ANC, Do nothing
    (`streamValueToIndexMap` = {0,3,4,1,2,7,5,−2}). Volume is **not** offered here.
  - Stream, col 7 (double-tap-hold): Volume UP, Volume Down, Do nothing.
  - Call, cols 1/2/3/4/5: Pick up/Hang up, Answer/Switch, Reject, Do nothing.
  - Call, col 7: Volume UP, Volume Down, Do nothing.
  - UNCERTAIN: the double-tap stream dialog uses the static `streamDoubleTapValueIndexMap`, which T1V2 never fills
    (left over from other device classes). Offering the normal stream list for double tap is the sensible choice.
- Factory defaults (`getDefaultSettingsV3Left/Right()` then `disable3Tap()`), "Reset" writes these with `A3`:
  ```
  LEFT  A: 00 FE FE FE 01 FE 06   B: 02 FE FE FE FE FE 06   C: FE 03 FE FE 01 FE 06   D: FE 04 FE FE 02 FE 06
        E: 00 03 0B 07 05 08 0A   F: 00 FE 0B 07 05 08 FE
  RIGHT A: 00 FE FE FE 01 FE 05   B: 02 FE FE FE FE FE 05   C: FE 03 FE FE 01 FE 05   D: FE 04 FE FE 02 FE 05
        E: 00 03 0B 07 05 08 09   F: 00 FE 0B 07 05 08 FE
  ```
  i.e. single = play/pause & answer/hang-up, double = next track, triple = Smart Switch (not remappable),
  triple-hold = Game Mode, hold 2 s = ANC toggle & reject, hold 4 s = pairing, double-tap-hold = volume down (L) / up (R).
  Full SET for left default: `A3 00 2A 00 FE FE FE 01 FE 06 02 FE FE FE FE FE 06 FE 03 FE FE 01 FE 06 FE 04 FE FE 02 FE 06 00 03 0B 07 05 08 0A 00 FE 0B 07 05 08 FE`.

### 3.13 Wear status & fit test (`FitTest`, `HammerheadT1V2`)
- Wear: GET `50 00 00` → `resp[3]` = left bud in ear (>0), `resp[4]` = right bud in ear (>0).
  Unsolicited `50 02 …` on insert/remove (same layout).
- Fit status: GET `51 00 00` → `resp[3]` = left good fit (>0), `resp[4]` = right good fit (>0).
- Fit test: `D0 00 00` start (success `resp[3] > 0`, 1000 ms × 2); the device plays the test tone and sends
  `51 …` notifications; the app waits **8600 ms** and uses the last `0x51` notification as the result.
  If either bud is removed during the test (wear notification) the app sends cancel `D1 00 00`.
  Before starting, the app checks both buds are worn.

### 3.14 Chroma lighting (`ChromaEffectHelperHammerheadT2v2`, `ChromaBLEV2FirmwareEffectFactory`)
One zone (`chromaZoneIds = [0]`, "primary"); effects offered: Static, Breathing, Spectrum, Audio Meter
(`ChromaEffectType.Static, BreathingWithFirmare, Spectrum, AudioReactiveFirmware`); app default = static white, brightness 255.
- GET effect `40 00 00` → `40 00 LL ee [r g b]…`; effect = `resp[3]`; colours start at `resp[4]`, count `(LL-1)/3`.
- GET brightness `41 00 00` → `resp[3] & 0xFF` (0–255).
- SET brightness `C1 00 01 bb` (0–255).
- SET effect (all `C0 00 LL ee …`, 3000 ms × 2, retried once after 100 ms if `resp[3] != 0`):

| Effect byte `ee` | Name | Command |
|---|---|---|
| `0x00` | Off | `C0 00 01 00` |
| `0x01` | Spectrum (colour cycle) | `C0 00 01 01` |
| `0x02` | Breathing | random colours `C0 00 01 02`; 1 colour `C0 00 04 02 r g b`; 2 colours `C0 00 07 02 r1 g1 b1 r2 g2 b2` |
| `0x03` | Static | `C0 00 04 03 r g b` |
| `0x04` | Audio Meter (audio reactive) | `C0 00 LL 04 [r g b]×n`, n ≤ 7, `LL = 3n+1` |
| `0xFF` | On (restore last effect) | `C0 00 01 FF` (sent by the dashboard on/off switch) |

  On read-back, `0x05` and unknown values fall through to "Spectrum" in the app (UNCERTAIN meaning).
  Dashboard on/off switch: Off → `C0 00 01 00` (up to 4 attempts); On → `C0 00 01 FF`, then re-read effect + brightness.

### 3.15 Voice-prompt language (`LanguageSettings`)
- Read-only over BLE for T1V2: current language = last byte of the `02` firmware response.
- Values: `ENGLISH 0`, `CHINESE 1` (简体中文), `FRENCH 2`, `GERMAN 3`, `JAPANESE 4`, `KOREAN 5`, `SPANISH 6`
  (T1V2 list order: German, English, French, Japanese, Chinese, Korean, Spanish).
- Changing language: `hasLanguageSwitch()` is false for T1V2, so the app does **not** send a command; it installs a
  language-specific firmware/voice-prompt image via OTA (`onLanguageChangedInstall`). The default `0xA4 00 02 hi lo`
  SET-language command is not used for this device. Out of scope for a control app.

### 3.16 Low Power Mode (HyperSpeed power mode)
- Only when firmware ≥ `01.04.04.03` (`hasLowerHSPowerMode`). Not in the `features` list; the presenter adds the item dynamically.
- GET `2F 00 00` → `lowPowerMode = resp[3]`; SET `AF 00 01 vv` (`LowPowerSettings`: `LOW_POWER_OFF 0`, `LOW_POWER_ON 1`).
- Unsolicited change: notification with `data[1] == 2` (the handler keys on `0xAF`/(175) — UNCERTAIN whether the
  firmware sends `2F 02 …` or `AF 02 …`; handle both). Wear notifications `50 02` update the item's enabled state
  ("both in ear"). The UI forces it off when ANC is turned on.
- `hasLowerPowerDefault() = false` → default touch map is the non-low-power V3 variant.

### 3.17 Unsolicited notifications (byte1 = 0x02)
| byte0 | Meaning | Payload |
|---|---|---|
| `0x12` | ANC changed | `[3]` = ANC value (§3.4) |
| `0x21` | battery | `[3]`,`[4]` (§3.3) |
| `0x2A` | charging state | `[3] == 1` → charging |
| `0x2B` | game mode / quick settings changed | `[3]` (§3.8) |
| `0x2D` | quick-connect host event | §3.9 |
| `0x50` | wear status changed | `[3]` left, `[4]` right |
| `0x51` | fit-test result | `[3]` left fit, `[4]` right fit |
| `0x62` | tutorial gesture detected (`62 02 02 tt …`) | `tt`: 1 single, 2 double, 3 triple, 4 triple-hold, 5 long press, 6 double-hold; which bud = which GATT connection it arrived on |
| `0xE1` | tutorial timed out (`E1 02 01 …`) | – |
| `0xAF`/`0x2F` | low power mode changed | §3.16 |

### 3.18 Features NOT supported / not used for T1V2
- Volume get/set (`11` / `91`): no feature, `minVolumeControlFWVersion = "01.02.02.00"` is defined but never read
  anywhere in the app. `getIsMuted` is hard-coded `true` (no `1A` mute command).
- Range booster (`26`/`A6`): `supportedRangeBoosterSettings = null`.
- Mic EQ, sidetone, 3D audio, normalization, input/output source, AI mic: not supported (default no-op/`00 00 00`).
- Device/factory reset: no BLE command for T1V2 in the app.
- Other GETs that exist in the base class but are never sent to T1V2: `01` (edition id), `03` (PID), `10` (audio status),
  `20` (sleep), `31` (image version), `33` (RSSI: used only in OTA pre-checks, `resp[3..]-256` dBm), `63` (Synapse take-over).

---

## 4. Connection / initialisation

### 4.1 Two buds, two BLE links
Each bud advertises and is connected **separately** (own BLE address, own GATT connection, `requirePair = true`).
`RazerDeviceManager.connectTws(list)` connects both, then runs `updateFromDevice` on each. The **primary** bud
(GRS bit2 = 0) answers almost everything; the secondary bud is only asked for GRS and its touch map.
`getPrimary()` = first active device with `isPrimary`; `getTws(isLeft)` picks a bud by side for remapping.
The two buds' BLE addresses are identical except octet index 3 (`AudioTws.isRelated` zeroes `iArr[3]` before
comparing), which is how the app pairs them.

### 4.2 `HammerheadT1V2.updateFromDevice` order (per bud; P = only if this bud is primary)
1. `initTouchMappingValues()` (local).
2. P: `checkFirmware` (local/cloud, no BLE).
3. `32 00 00` GRS → sets isLeft/isPrimary/isStandalone (**fatal on failure**).
4. P: `2B 00 00` quick settings (fatal).
5. P: `12 00 00` ANC (fatal).
6. P: `22 00 00` auto pause (fatal).
7. `23 00 00` touch map (both buds, fatal).
8. P: `02 00 00` firmware version (fatal).
9. P: `21 00 00` battery (fatal).
10. P: `13 00 00` EQ (fatal).
11. P: `2D 00 00` quick-connect hosts (non-fatal).
12. P: `2C 00 00` timeout (fatal).
13. P: `27 00 00` auto-switch / DND (fatal).
14. P: chroma: `40 00 00` (+ `41 00 00` inside), then `41 00 00` again (fatal).
Then for the primary, `RazerDeviceManager` additionally sends `02 00 00` again (`updateLanguageValueAndCompleteFirmwares`).
Later, screens read on demand: `15` (EQ bands, when Custom shown), `50`/`51` (fit/wear), `2F` (low power, if fw allows).
No handshake or "set primary" command is required.

### 4.3 Classic ↔ BLE address
`AudioTws.getClassicAddress(ble)`: octet[3] − 1 (`autoSystemPairMinus1 = true`), e.g. BLE `AA:BB:CC:DE:EE:FF` →
classic `AA:BB:CC:DD:EE:FF`. The app bonds the classic address (A2DP/HFP) of the lowest BLE address after first
BLE connect. On Windows: the paired classic device's address + 1 in octet 3 gives the BLE address(es) to look for
(UNCERTAIN which bud's classic address Windows sees).

---

## 5. Identification during scan
- Scan filter: service UUID `0xFD65` (`createScanFilter`), and in the add-device screen manufacturer id
  **1678 = 0x068E (Razer)** (`ScanDevicePresenter.MANUFACTURER_ID`).
- Manufacturer data (first entry, `keyAt(0)`), as seen by Android (company id stripped):
  ```
  [0] productType = 0x08
  [1] modelId     = 0x15 (21)
  [2] editionId   = 0x00   (must equal 0 for this class; other editions = other classes)
  ...
  [last] GRS byte (§3.1: bit0 side, bit1 standalone, bit2 primary)
  ```
  `initFromScanResult` throws unless `data[0]==productType && data[1]==modelId` and (`editionId > -1` →
  `data[2]==editionId`). Bytes between [2] and [last]: unknown (UNCERTAIN).
  On WinRT: `BluetoothLEAdvertisement.ManufacturerData` entry with `CompanyId == 0x068E`, `Data` = bytes above.

---

## 6. UI (what the app shows for this device)

`features = [TUTORIAL, EQ_SETTINGS, AUTO_PAUSE_SETTINGS, LANGUAGE_CHANGE, TOUCH_MAPPING, ANC_SETTINGS,
GAMING_MODE, FIT_TEST, NEW_QUICK_CONNECT, DND, CHROMA_SETTINGS]`; dashboard items are added by presenters when the
matching `supported*` list is non-null.

| Dashboard item (label) | Control | Commands |
|---|---|---|
| Battery L / R rings | display (+ charging) | `21`, notif `2A` |
| Active Noise Cancellation | bottom sheet: Off / On (+ "ANC Level" slider 1–10) / Ambient; text "Turn on ANC to control how much ambient noise is nullified or use ambient for a heightened sense of awareness." | `12`/`92`, needs buds worn (`50`) |
| Equalizer | Default (THX), Amplified, Vocal, Enhanced Bass, Enhanced Clarity, Custom (10 bands) | `13`/`93`, `15`/`95` |
| Chroma Effect | on/off switch + picker (Static, Breathing, Spectrum, Audio Meter) + brightness | `40`/`C0`, `41`/`C1` |
| Game Mode (description "Active"/"Disabled") | switch | `2B`/`AB` (1 = on, 2 = off) |
| Quick Connect | host list (disabled if empty or Game Mode on) | `2D`, `AD` |
| Do Not Disturb — "Block calls while gaming in 2.4 GHz" | switch | `27`/`A7` |
| Auto Pause / Resume — "Auto Pause when off ears" | switch | `22`/`A2` |
| Remap | per-ear COMMON / CALL gesture editor; triple tap greyed out | `23`/`A3` per bud |
| Language | list; change = firmware install | from `02` |
| Low Power Mode | switch, only fw ≥ 01.04.04.03 | `2F`/`AF` |
| Fit test (onboarding + ANC screen) | test screen | `50`, `51`, `D0`, `D1` |
| Tutorial (`HammerheadT1v2Tutorial`) | gesture trainer | `E0`, `E1`, notif `62` |
| Firmware update | OTA (`FirmwareUpdateAmeliaT1v2HS`, `.zip`) | out of scope |

Not shown: Auto Standby (timeout), volume, mic settings, range booster.

Firmware-version gates in code: `hasLowerHSPowerMode` ≥ `01.04.04.03`; `forceEnableAncSwitching` ≥ `01.02.00.00`
(unused); `MIN_VOLUME_CONTROL_VERSION = 01.02.02.00` (unused). Comparisons are lexicographic on the zero-padded
`"NN.NN.NN.NN"` string.
