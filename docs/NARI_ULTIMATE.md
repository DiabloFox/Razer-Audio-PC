# Razer Nari Ultimate — how Synapse drives it, and what RazerAudioPC needs

Reverse-engineered from Razer Synapse 4 (RazerAppEngine `app-4.0.896`, Nari module
`project-anne-hopet1`, mw `main.25659995f9ac02a8bfab.js`, ui `main.4cb42513.js`) and checked on a
Nari Ultimate (edition 0, headset firmware 0.1.0.6) with Synapse's own logs.

| Document | Scope |
|---|---|
| **this file** | architecture, every Synapse control mapped to its mechanism, defaults and presets, lighting effects, lifecycle, implementation plan |
| [NARI_ULTIMATE_HID.md](NARI_ULTIMATE_HID.md) | the USB HID protocol: transport, frame format, every command byte for byte, events, verification captures |
| [NARI_ULTIMATE_AUDIO.md](NARI_ULTIMATE_AUDIO.md) | the PC-side audio processing (THX APO / `ThxV3Native` DLL), Windows endpoints, and how to replace it |

"UNCERTAIN" marks inferences.

---

## 1. Architecture

```
 Nari Ultimate headset  ──2.4 GHz──  USB dongle 1532:051A
   (haptics, LEDs, side tone,          ├─ MI_00  audio "Razer Nari Ultimate - Chat"  (render + mic capture)
    power-off timer, battery,          ├─ MI_03  audio "Razer Nari Ultimate - Game"  (render)
    mic mute, volume wheel)            └─ MI_05  HID: COL01 consumer keys, COL02 events, COL03 commands

 Synapse 4
   ├─ product "mw" (JavaScript, AudioAvneraDevice) ── HID feature report 0xFF ──► headset/dongle
   ├─ THX layer (AudioEffectsTHXV3 → ThxV3Native_v1.0.x.dll) ──► THX APO on the Game/Chat endpoints
   │                                                             + Windows endpoint volume/mute
   └─ lighting engine (RzLightingEngineApi.dll, 25 fps) ── custom-frame HID writes ──► headset LED
```

The headset does far less than the Synapse UI suggests. **Only five features are hardware**:
haptics, side tone, auto power-off, LED brightness, and LED colour (one colour per write). It also
reports battery, link state and mic mute. **Everything on the Sound tab, most of the Mic tab, and all
lighting animations are computed on the PC.** The "REQUIRES RAZER SYNAPSE" badge on the equalizer
means exactly that: the EQ is applied by THX audio-processing modules (APOs) inside Windows' audio
engine, not by the headset.

The THX parts are a **separate install**: the "THX Spatial Audio USB 1532-051A" driver package, the
`VSSrv` service and "THX V3 APO Presets" are their own Programs & Features entries. They stay when
Synapse is uninstalled and keep applying the last settings. Only Synapse's control DLL
(`ThxV3Native`) goes away. RazerAudioPC can therefore drive the hardware features over HID with
nothing installed, and drive the THX features by talking to `VSSrv` when that package is present
([NARI_ULTIMATE_AUDIO.md](NARI_ULTIMATE_AUDIO.md) §6–7).

---

## 2. Feature map (every control in the Synapse UI)

HW = HID command ([NARI_ULTIMATE_HID.md](NARI_ULTIMATE_HID.md)); PC = THX/Windows
([NARI_ULTIMATE_AUDIO.md](NARI_ULTIMATE_AUDIO.md)). The defaults come from the module's DEFAULTPROFILE (§5).

### Sound
| Control | Range | Default | Synapse event | Mechanism |
|---|---|---|---|---|
| Stereo / THX Spatial Audio | toggle | THX on | `ON_SET_SPATIAL_ENABLE` | PC: THX `SetRenderSpatialProcessing` |
| Equalizer Game / Movie / Music / Custom | 10 bands 31 Hz–16 kHz, −12…+12 dB, step 1 | Game | `ON_SET_AUDIO_EQUALIZER` | PC: THX `SetRenderPreset("Game Mode" / "Cinema Mode" / "Music Mode" / "Custom")`, `SetRenderBatch(true)`, `SetRenderEQGains("[10 dB]")`, `SetRenderBatch(false)` |
| Per-game THX presets / "Add Games & Programs" | app list | none | `ON_SET_ACTIVE_AUDIO_EQUALIZER` (on foreground-app change) | PC: THX preset by name (`renderPresetList`, e.g. `ApexLegends_cmm`) |
| Sound properties | button | – | – | opens the Windows sound control panel |

### Enhancement
| Control | Range | Default | Synapse event | Mechanism |
|---|---|---|---|---|
| Haptic Intensity | switch + 20…100 step 10 | on, 50 | `ON_SET_HAPTIC_INTENSITY_ENABLED` / `_VALUE` | **HW** Set Haptics Control `FF 04 02 F1 06 20 en lo hi 00` (HID §4.6) |
| Bass Boost | switch + 10…100 | off, 55 | `ON_SET_BASS_BOOST` | PC: THX `SetRenderBassBoost` / `…Level` |
| Sound Normalization | switch + 10…100 | off, 55 | `ON_SET_SOUND_NORMALIZATION` | PC: THX `SetRenderNormalization` / `…Level` |
| Voice Clarity | switch + 10…100 | off, 55 | `ON_SET_VOICE_CLARITY` | PC: THX `SetRenderVocalClarity` / `…Level` |

### Mic
| Control | Range | Default | Synapse event | Mechanism |
|---|---|---|---|---|
| Microphone (switch = mute) + volume | 0…100 | on, 50 | `ON_SET_MIC_VOLUME` | PC: Windows capture endpoint mute/volume (THX `SetCaptureMuteBySystem` / `SetCaptureVolumeBySystem`). The headset's own mute button is reported by event `0x65` (HID §6) |
| Voice Gate | checkbox + 1…5 | off | `ON_SET_MIC_SENSITIVITY` | PC: THX `SetCaptureVoiceGate` / `SetCaptureVoiceGateLevel(20 × v)` |
| Mic Preview | toggle | off | `ON_SET_MIC_PREVIEW` | PC: THX `SetCapturePreview` (mic played back through the APO) |
| Volume Normalization | checkbox + 10…100 | off, 55 | `ON_SET_MIC_VOLUME_NORMALIZATION` | PC: THX `SetCaptureNormalization` / `…Level` |
| Vocal Clarity | checkbox + 10…100 | off, 55 | `ON_SET_MIC_VOCAL_CLARITY` | PC: THX `SetCaptureVocalClarity` / `…Level` |
| Mic Noise Cancellation | checkbox + 10…100 | off, 55 | `ON_SET_MIC_AMBIENT_NOISE_REDUCTION` | PC: THX `SetCaptureNoiseReduction` / `…Level` |
| Mic Equalizer Default / Mic Boost / Broadcast / Conference / Custom | 10 bands, −12…+12 dB | Default | `ON_SET_MIC_EQ` | PC: THX `SetCaptureEQGains` |
| Mic Monitoring (Side Tone) | switch + 0…100 | off, 50 | `ON_SET_MIC_SIDE_TONE` | **HW** Set Side Tone `FF 04 0E F1 05 01 04 00 vv` (HID §4.4), after THX `SetStartCaptureStatus(on)` opens a mic stream |

Tooltip worth copying: side tone keeps the microphone active, so the headset won't enter wireless
power saving while it is on.

### Lighting
| Control | Range | Default | Synapse event | Mechanism |
|---|---|---|---|---|
| Brightness switch + slider | 0…100 (0 = off) | on, 100 | `ON_SET_BRIGHTNESS`, `ON_CHANGE_ENABLE_STATE` | **HW** Set Chroma Brightness `FF 04 12 F1 03 71 bb`, `bb = trunc(255 × v / 100)`, `v = 0` when off |
| Quick Effects: Static / Breathing / Spectrum Cycling / Audio Meter | §3 | Spectrum Cycling | `ON_SET_EFFECTS` | **PC-rendered**, streamed as Set Chroma Custom Frame `FF 04 12 F1 05 72 r g b` at 25 fps |
| Advanced Effects | Chroma Studio | – | – | same streaming path |
| Switch off lighting: when display is turned off | checkbox | off | `ON_SWITCH_OFF_SETTING` | brightness 0 on display-off, restore on display-on (dimming ignored) |
| (hidden) Switch off when idle for N min | 1…15 | off, 1 | `ON_SYSTEM_IDLE` | brightness 0 after N minutes idle (`hideLightingIdle` hides it in the UI) |

### Power
| Control | Range | Default | Synapse event | Mechanism |
|---|---|---|---|---|
| Power Saving | switch + 15…60 min, step 1 | on, 15 | `ON_SET_POWER_SAVING` | **HW** Get, then Set Silence Detection Control `FF 04 12 F1 05 41 en lo hi`, period in **seconds** (minutes × 60) |

### Status / Help
| Item | Mechanism |
|---|---|
| Battery % and charging icon | HW Get Battery Status, `data[5]` % (coarse: 30/50/80/100), `data[0] == 5` charging; polled every 15 s (10 s while charging) |
| Connected / headset off | HW Get Link Status + event `0x64` |
| Firmware version "0.1.0.6", serial | HW Get Extended Firmware Version, Get Serial Number |
| Demo tab | THX video only |
| Firmware update | none: Synapse has no firmware info for this product (`Device has no latest firmware info`) |

---

## 3. Lighting effects (as Synapse renders them)

The headset has one LED colour (`ledConfig` 1×1). Synapse's lighting engine computes the colour on
the PC at **25 fps** and sends it with Set Chroma Custom Frame. Colours below are RGB. Gradient
stops are percentages of one cycle, interpolated by `RzLightingEngineApi.dll` (UNCERTAIN: assumed
linear RGB interpolation).

| Effect (UI id) | Engine effect | Parameters |
|---|---|---|
| Static (1) | 6 | constant `color1` (default `#00FF00`) |
| Breathing (2), one colour | 5 | stops `0: black, 25: black, 50: C1, 75: C1, 100: black`, **7000 ms** per cycle, repeat forever |
| Breathing, two colours | 5 | stops `0: black, 12: black, 25: C1, 38: C1, 50: black, 62: black, 75: C2, 88: C2, 100: black`, **14000 ms** per cycle |
| Breathing, random | 5 | mode 1, no stops: the DLL picks the colours (UNCERTAIN: random colour per breath) |
| Spectrum Cycling (3) | 4 | **37740 ms** per cycle, stops `0 #FF18FF, 18 #FF1010, 28 #FF8000, 35 #EFFF00, 42 #DFFF00, 49 #00FF00, 52 #00FFFF, 64 #1858FF, 77 #1858FF, 100 #FF18FF` |
| Audio Meter (12) | 12 | stops `0 #00FF00, 50 #FFFF00, 100 #FF0000`; `BoostLevel` = UI "Color Boost" 0.25…4 step 0.25 (default 1); decay 1000 ms; source = playback (loopback of the default render device, UNCERTAIN). With one LED: colour = gradient at the current peak level (UNCERTAIN) |

Defaults: static `#00FF00`; breathing `#00FF00`, second colour none, not random; audio meter boost 1.
On exit Synapse leaves the headset on static green (`FF 04 12 F1 06 70 01 00 FF 00`).

Suggested RazerAudioPC implementation: a 40 ms `DispatcherTimer` or background loop computing
`t = (now − start) mod duration`, interpolating between stops, scaling nothing (brightness is
separate, in hardware), and sending the frame only when the colour changed. For Audio Meter, use a
WASAPI loopback capture on the Game endpoint and map peak level × boost (with 1 s decay) onto the
gradient.

---

## 4. Lifecycle (what to replicate)

1. **Find** the dongle (`VID_1532&PID_051A&MI_05`). Open `COL03` (commands) and `COL02` (events) (HID §7.1).
2. **Wait for the dongle**: Get Link Status; while `6` (InitialScan) or no answer, retry every 1 s.
3. **Headset off** (`3`, Searching): show "headset off", send no headset commands, wait for event `0x64 = 1`.
4. **Headset on** (`1`, Active): read firmware, serial, edition and battery, then apply the profile:
   - **Hardware:** brightness, haptics, side tone (open the mic stream first if enabled), power saving.
   - **Lighting:** set mode `08`, start the frame loop.
   - **PC:** EQ, spatial, enhancements and mic effects through the audio layer.
5. **Run**:
   - battery every 15 s (10 s while charging);
   - on `0x65` events mirror the mic mute to the Windows Chat capture endpoint;
   - on `0x64 ≠ 1` show "headset off" and pause the lighting loop;
   - on `0x64 = 1` re-apply the hardware settings (Synapse only does this once per session, but it
     is cheap and safe).
6. **Display off** (if enabled): brightness 0, pause frames; display on: restore.
7. **Exit**: optionally leave a static colour (Synapse uses green), so the LED doesn't freeze on an
   arbitrary frame.

---

## 5. Default profile and presets

DEFAULTPROFILE in the mw. It is identical for editions 0, 128 (Overwatch Lúcio) and 129 (PewDiePie);
only names and images differ. Synapse renames it `<computer name>-Default`.

```json
{ "brightness": {"isEnabled": true, "value": 100},
  "switchOffLighting": {"isDisplayOn": false, "isIdleEnabled": false, "idleMinutes": 1},
  "quickEffects": {"selectedEffectId": 3},
  "bassBoost": {"isEnabled": false, "value": 55},
  "soundNormalization": {"isEnabled": false, "value": 55},
  "voiceClarity": {"isEnabled": false, "value": 55},
  "hapticIntensity": {"isEnabled": true, "value": 50},
  "micSideTone": {"isEnabled": false, "value": 50},
  "micVolumeNormalization": {"isEnabled": false, "value": 55},
  "ambientNoiseReduction": {"isEnabled": false, "value": 55},
  "micVoiceClarity": {"isEnabled": false, "value": 55},
  "micSensitivity": {"isEnabled": false, "value": 50},
  "micVolume": {"isEnabled": true, "value": 50},
  "audioEqualizer": {"selectedMode": "standard", "presets": {"selectedPreset": {"standard": "game"}}},
  "thxSpatialAudio": {"isEnabled": true},
  "applicationPresets": [] }
```
The device level adds `powerSaving {isEnabled: true, value: 15}` (minutes).
`micSensitivity.value` 50 lies outside the Voice Gate slider range 1…5; clamp it.

**Playback EQ** (dB) at 31, 63, 125, 250, 500, 1k, 2k, 4k, 8k, 16k Hz. These are the profile tables
Synapse actually applies; the logs show `setRenderEQGains(2,2,1,1,2,3,3,3,1,0)` for Music.

| Preset | Gains (dB) |
|---|---|
| Game | −3 −3 −4 0 5 5 4 1 0 −1 |
| Movie | 4 4 3 0 −3 −1 3 5 2 1 |
| Music | 2 2 1 1 2 3 3 3 1 0 |
| Custom | 0 0 0 0 0 0 0 0 0 0 |

(A second table `audioBandPreset`, Game `3 4 5 0 0 0 4 3 4 2`, Movie `3 3 3 1 4 3 3 1 3 3`,
Music `4 4 4 0 0 1 2 3 4 0`, only seeds a legacy path; it is probably unused with THX v3 (UNCERTAIN).)

**Mic EQ** (dB). The mw labels the bands 125, 250, 500, 750, 1k, 2k, 3k, 4k, 5k, 6k Hz; the UI draws
them on the playback frequencies. Only the gains are passed to THX.

| Preset | Gains (dB) |
|---|---|
| Default | 0 0 0 0 0 0 0 0 0 0 |
| Mic Boost | 0 2 3 4 5 5 5 4 3 1 |
| Broadcast | 4 4 4 3 −2 −7 −4 −2 −3 −5 |
| Conference | −8 −7 −5 −3 −1 1 3 2 1 0 |
| Custom | 0 0 0 0 0 0 0 0 0 0 |

---

## 6. Implementation plan for RazerAudioPC

The app is currently built around one `HammerheadDevice` (BLE). A Nari backend fits next to it:

| Piece | Suggested file | Notes |
|---|---|---|
| HID transport | `Hid/NariHid.cs` | SetupDi enumeration, `CreateFile`, `HidD_SetFeature` / `HidD_GetFeature` (64 bytes, id `0xFF`), one `SemaphoreSlim` around each transaction, overlapped reader on `COL02` for events (HID §7) |
| Protocol | `Hid/NariProtocol.cs` | the command table from HID §4.0 as `byte[]` constants plus parsers (battery, firmware ASCII, serial, LE16 helpers, side-tone ↔ % conversion) |
| Device model | `Device/NariDevice.cs` | `ObservableObject` like `HammerheadDevice`: `LinkState`, `Battery`, `Charging`, `Haptics{On,Level}`, `SideTone{On,Level}`, `PowerSaving{On,Minutes}`, `Brightness{On,Value}`, `Lighting` effect; re-apply on link-up |
| Lighting | `Device/NariLighting.cs` | 25 fps effect renderer (§3); WASAPI loopback for Audio Meter |
| Audio (PC side) | `Audio/…` | per [NARI_ULTIMATE_AUDIO.md](NARI_ULTIMATE_AUDIO.md): mic mute/volume through Core Audio (`IAudioEndpointVolume`) works without anything installed; EQ / bass boost / normalization / clarity / spatial need either the THX APO (if present) or a replacement DSP |
| UI | pages | a device switcher in `MainWindow`, or make the pages depend on a small `IAudioDevice` interface |

Settings without Synapse: persist the profile yourself (`AppSettings`), since the headset only stores
the five hardware values (UNCERTAIN, HID §5.1) and nothing PC-side.

Things not to copy from Synapse:
- It never sends effect `08` (it throws first); send it.
- It re-sends power saving twice because of a typo in its change check (`peroidSec`).
- Its combined `ON_SET_HAPTIC_INTENSITY` path calls a method the Nari class doesn't have (fails silently).
- It polls Get Edition Information two or three times per setup.
- It doesn't re-apply hardware settings after a headset power cycle.
