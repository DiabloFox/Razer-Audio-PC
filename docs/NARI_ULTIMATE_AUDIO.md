# Razer Nari Ultimate — PC-side audio features (THX Spatial Audio v3)

Everything on Synapse's **Sound** and **Enhancement** tabs except Haptic Intensity, and everything
on the **Mic** tab except Mic Monitoring, is processed on the PC. None of it is a headset command.
This file documents how Synapse 4 drives that processing, so RazerAudioPC can do the same, or
replace it.

Sources: the Nari module's THX wrapper (`mw/AudioEffectsTHXV3.fa69465f8f88bce2ecc4.js`) and shared
middleware (`mw/3487.6f6624563a3fedb88bbc.js`); static analysis (exports, strings, disassembly) of
`ThxV3Native_v1.0.41.1.dll`, `VSSrv.exe` (embedded typelib) and the THX APO DLLs; the THX INF files
in the DriverStore; read-only `reg query`; and Synapse's logs (`ThxV3Native*.log`,
`THXDLL_V3CommonSubProcess*.log`, `products_1306_mw*.log`). No setter was called during the analysis.

"UNCERTAIN" marks inferences. Hardware features are in [NARI_ULTIMATE_HID.md](NARI_ULTIMATE_HID.md);
the overall map is in [NARI_ULTIMATE.md](NARI_ULTIMATE.md).

---

## 1. Components

```
Synapse (Electron)
  └─ ffi ─► ThxV3Native_v1.0.41.1.dll         %LocalAppData%\Razer\RazerAppEngine\User Data\Apps\Common\thxv3common\
               │  (Synapse loads it in an FFI sub-process, isolation group "THXDLL_V3Common")
               ├─ render settings ─ ZeroMQ + protobuf ─► VSSrv
               ├─ mic settings ──── COM IVSSrvSettings ─► VSSrv
               └─ Windows Core Audio (endpoint volume/mute, default device, a dummy capture stream)

VSSrv.exe  (THX service "VSSrv", LocalSystem, automatic; also the COM LocalServer)
  ├─ persists state: HKCU\SOFTWARE\THX\SpatialAudio\UserState\*, HKCU\SOFTWARE\VisiSonics\*
  └─ signals Global\ events → the APOs pull state from it over COM:
       THXOutAPO-SSE2-v3.dll  SFX/MFX on the Nari "Game" and "Chat" render endpoints
       THXMicAPO-SSE2-v3.dll  EFX on the Nari "Chat" capture endpoint (microphone)
```

### 1.1 Installed packages (this PC)
Programs & Features lists these separately from "Razer Synapse 4.0.896" (verified):

| Product | Version | Contents |
|---|---|---|
| THX Spatial Audio USB 1532-051A | 3.2.3.0 | extension INFs `THXUSBAPO-Game.inf` (for `USB\VID_1532&PID_051A&MI_03`) and `THXUSBAPO-Chat.inf` (`MI_00`), `THXRTAPO.inf` (the APO DLLs into System32), `THXRTSVC.inf` (VSSrv.exe, VSSrvInit.exe, VSHelper.exe + service), `THXRTSCU.inf` (spatial-config-util) |
| THX V3 APO Presets | 3.2.12 / 3.2.14 / 3.2.16 / 3.2.18 | `C:\ProgramData\THX\USB\1532\051A\thx-headphones-{spatial,stereo}-<preset>-usb-1532-051a-V3.json` (271 files) + `thx_spatial.conf.json` |

Synapse installs them silently from `THX-051A-RazerNariUltimate-v3.0.37.0.exe /S /LAUNCH` and
`THX-051A-Presets-NariUltimate-v3.2.x.exe /S /LAUNCH`, then deletes the installers
(`installer[1306].log`).

Registered APO CLSIDs (from the INFs, matching the live `FxProperties`):
render SFX `{C792E395-BD30-472D-9A2F-2783D7FAF812}`, render MFX `{68650828-CA0F-4196-9096-5F50931AF10A}`,
capture EFX `{868989EE-5D2F-42AE-A7E4-EA830D24A684}`. Endpoint property
`{D5E8F0AB-4DE6-4D91-AB21-68868DDA6A4A},0 = "VSHP"`, `,1 = %ProgramData%\THX\usb\1532\051A\thx_spatial.conf.json`.

### 1.2 What survives uninstalling Synapse
- **Stays**: the THX packages above (separate products): the DriverStore packages, the APOs, VSSrv,
  the presets, and the last saved state. Audio keeps being processed with the **last settings**.
  Synapse's `uninstall.xml` has no THX entries (UNCERTAIN whether removing the Nari module does).
- **Goes away**: `ThxV3Native.dll` (it lives in Synapse's AppData), Synapse's profiles, and the
  dummy capture stream Synapse keeps open for side tone (§5.3).
- So after uninstalling Synapse the THX effects are still there, and RazerAudioPC can control them
  only by talking to **VSSrv** itself (§6), or by shipping a copy of the DLL (licensing question).
  Without the THX package at all, the app needs its own DSP (§7).

---

## 2. Windows endpoints

| USB interface | Endpoint name (Windows) | Use |
|---|---|---|
| `MI_03` | "Headphones (… Razer Nari Ultimate - Game)", render | game audio; **THX spatial/EQ target** |
| `MI_00` | "Headset Earphone (… Razer Nari Ultimate - Chat)", render | chat audio |
| `MI_00` | "Headset Microphone (… Razer Nari Ultimate - Chat)", capture | microphone; THX mic APO |

- Endpoint ids are ordinary `IMMDevice::GetId()` strings, e.g.
  `{0.0.0.00000000}.{17281951-e2fb-451f-b584-11f3deb1f07f}` (render) and `{0.0.1.00000000}.{…}`
  (capture). Synapse picks them by **container id + name**: the render endpoint whose name contains
  `"Razer Nari Ultimate - Game"`, and the capture endpoint whose name contains
  `"Razer Nari Ultimate - Chat"` (`3487` `wt` / `kt`).
- **Container id** = `PKEY_Device_ContainerId {8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C},2` of the
  endpoints, or `DEVPKEY_Device_ContainerId` of `USB\VID_1532&PID_051A`, e.g.
  `{356622E0-3D9C-11F1-AF56-7085C2428B0E}`. It **changes** when Windows re-enumerates the dongle
  (7 different values appear in the logs). Look it up every time and pass it upper case with braces.
- **THX only processes the current output device.** VSSrv follows its "output device"
  (`HKCU\SOFTWARE\VisiSonics\OutputDeviceId`). Synapse skips the EQ when the Game endpoint isn't the
  default (`3487` 88517). Its `SetDefaultRenderDevice` sets both the console and the
  communications default (log: `set eCommunications r: 1`; presumably via the undocumented
  `IPolicyConfig`, UNCERTAIN).

---

## 3. The ThxV3Native API (what Synapse calls)

### 3.1 Calling convention
Exports are plain C functions (`cdecl`, x64). The first argument of almost every export is the
**container id** string.

| ffi type | Native | C# P/Invoke |
|---|---|---|
| `string` | UTF-8 `const char*` | `[MarshalAs(UnmanagedType.LPUTF8Str)] string` |
| `bool` | 1-byte C++ `bool` | `[MarshalAs(UnmanagedType.I1)] bool` (arguments and returns) |
| `int` / `float` / `double` | int32 / float32 / float64 | `int` / `float` / `double` |
| `pointer` (return) | heap UTF-8 JSON `char*` | `IntPtr` → `Marshal.PtrToStringUTF8`; free with the exported `FreeMalloc` (UNCERTAIN for every function) |

- The DLL statically links its CRT and imports only system DLLs. It always logs to
  `%LocalAppData%\Razer\RazerAppEngine\User Data\Logs\ThxV3Native.log`.
- Calls block. They run on an internal executor with a timeout
  (`RunIntTaskInThread wait timeout!!!`), and `SetRenderBatch(false)` takes about 300 ms. Call it
  off the UI thread.

### 3.2 Initialisation (`AudioEffectsTHXV3.initElectron`, `3487` 79563–79608)
1. Make sure service **`VSSrv`** is running (Synapse starts it if not).
2. `GetDLLVersion()` → `"1.0.41.1"`.
3. `SetNodeFFIEvent(callback)`: `void cb(const char* json)`; keep the delegate alive. Events:
   `Razer-ThxV3Native-Device-Update` and `Razer-ThxV3Native-Volume-Notify`
   `{containerId, deviceId, isCapture, muted, volumeLevel, plugType, data, sidetoneLevel, sidetoneMuted}`.
4. `DeviceInitialize(containerId)`: opens Core Audio for the container, connects to VSSrv (COM +
   ZeroMQ) and sends `Register{pid}`. It fails ("Cannot get state for current device") when VSSrv's
   current output has no THX configuration, i.e. when the Nari Game endpoint isn't the output
   (UNCERTAIN, from the log).
5. `GetRenderDeviceList(cid)` / `GetCaptureDeviceList(cid)` → JSON
   `{"count":n,"devices":[{"deviceContainerId","id","name"}…],"result":true}`.
6. `SetCaptureDevice(cid, micEndpointId)`. This is **required before any `*Capture*` call**; the
   wrapper's comment says "to avoid crash". Synapse then calls `SetCaptureMute(cid,false)` and
   `SetCaptureVolume(cid,10.0)`: the fixed +10 dB mic-APO gain, see §5.1.
7. Use the setters (§4–§5).
8. Shutdown: `SetStartCaptureStatus(cid,false)`, `SetCapturePreview(cid,false)`,
   `DeviceTerminate(cid)`, `Terminate()`.

### 3.3 Export table
From the wrapper's FFI declaration. All take the container id first unless the row says otherwise.

| Group | Exports |
|---|---|
| lifecycle | `GetDLLVersion()`, `SetNodeFFIEvent(cb)`, `DeviceInitialize(cid)`, `DeviceTerminate(cid)`, `Terminate()`, `OpenAudioProperties()`, `OpenAudioMixer()` |
| devices | `GetAudioDeviceListBySystem(int)`, `GetAudioDeviceListBySystemEx(int, uint32)`, `GetDefaultAudioEndpoint()`, `GetRenderDeviceList`, `GetRenderDevice`, `SetDefaultRenderDevice(cid, id)`, `GetCaptureDeviceList`, `GetCaptureDevice`, `SetCaptureDevice(cid, id)` |
| render volume | `Get/SetRenderMute`, `Get/SetRenderVolume` (0–100), `Get/SetRenderVolumeDB`, `Get/SetRenderChannelVolume(cid, ch[, v])`, `…ChannelVolumeDB`, `GetRenderChannelCount`, `GetRenderVolumeRange`, `SetRenderVolumeStepUp/Down`, `Get/SetRenderMuteByEndpointId(cid, [bool,] endpointId)`, `Get/SetRenderVolumeByEndpointId` |
| spatial | `Get/SetRenderSpatialProcessing(cid, bool)`, `Get/SetRenderListeningMode`, `Get/SetRenderCustomRoomType`, `GetRenderRoomList`, `Get/SetRenderRoom`, `SetRenderRoomType`, `Get/SetRenderEmitterPosition`, `Get/SetAllRenderEmitterPosition`, `Get/SetHRTFState`, `ImportHRTFFile`, `GetCustomHRTFPresent`, `GetHeadphone5`, `Get/SetReverbTail`, `…Factor` |
| EQ / presets | `GetRenderPresetList`, `Get/SetRenderPreset(cid, name)`, `SetRenderPresetMode`, `SetRenderPresetEnhance`, `ResetRenderPreset`, `Get/SetRenderEQGains(cid, json)`, `ResetRenderEQGains`, `Get/SetRenderTilt`, `…TiltLevel`, `Get/SetHighShelfFrequency`, `Get/SetHighShelfGain`, `SetRenderBatch(cid, bool)`, `Get/SetRenderDSPParam(cid, i[, d])`, `Get/SetDSPProcessing` |
| enhancements | `Get/SetRenderBassBoost(cid, bool)`, `Get/SetRenderBassBoostLevel(cid, int)`, `Get/SetRenderNormalization`, `…NormalizationLevel`, `Get/SetRenderVocalClarity`, `…VocalClarityLevel` |
| mic DSP | `Get/SetCaptureHighpass`, `…HighpassLevel(float)`, `Get/SetCaptureVoiceGate`, `…VoiceGateLevel(float)`, `Get/SetCaptureNoiseReduction`, `…NoiseReductionLevel(int)`, `Get/SetCaptureNormalization`, `…NormalizationLevel(int)`, `Get/SetCaptureVocalClarity`, `…VocalClarityLevel(int)`, `Get/SetCaptureEQ(bool)`, `PresetCaptureEQ(int)`, `Get/SetCaptureEQGains(cid, json)`, `Get/SetCaptureParam(cid, i[, d])`, `Get/SetCapturePresetList/Preset`, `Get/SetCapturePreview(bool)` |
| mic level | `Get/SetCaptureMute(bool)`, `Get/SetCaptureVolume(float)` (THX mic gain), `Get/SetCaptureMuteBySystem(bool)`, `Get/SetCaptureVolumeBySystem(int)` (Windows endpoint) |
| side tone (not used on the Nari) | `Get/SetCaptureSidetone`, `…SidetoneLevel`, `…SidetoneBySystem`, `…SidetoneLevelBySystem`, `SetCaptureSidetoneConfig(cid, json)` |
| capture keep-alive | `Get/SetStartCaptureStatus(cid, bool)` |

---

## 4. Sound and Enhancement tabs

### 4.1 Calls per control
| UI control | Synapse logic | DLL calls (in order) |
|---|---|---|
| Stereo / THX Spatial Audio | toggle, then re-apply the EQ (presets exist per spatial state) | `SetRenderSpatialProcessing(cid, on)`, then the EQ sequence below |
| EQ preset Game / Movie / Music / Custom | `game → "Game Mode"`, `music → "Music Mode"`, `movie → "Cinema Mode"`, otherwise `"Custom"` (`3487` `Ct`) | `SetRenderPreset(cid, name)` → `SetRenderBatch(cid, true)` → `SetRenderEQGains(cid, "[g0,…,g9]")` → `SetRenderBatch(cid, false)` |
| EQ band drag (Custom or any preset) | same sequence with the edited gains | as above |
| Per-game THX preset | foreground-app change → `ON_SET_ACTIVE_AUDIO_EQUALIZER` | `SetRenderPreset(cid, "ApexLegends_cmm")` **only**: the curated preset keeps its own EQ |
| Bass Boost | change the switch if it differs; set the level only when enabled **and** changed | `SetRenderBassBoost(cid, on)`, `SetRenderBassBoostLevel(cid, 0…100)` |
| Sound Normalization | same pattern | `SetRenderNormalization(cid, on)`, `SetRenderNormalizationLevel(cid, 0…100)` |
| Voice Clarity | same pattern | `SetRenderVocalClarity(cid, on)`, `SetRenderVocalClarityLevel(cid, 0…100)` |

- `GetRenderPresetList(cid)` → `{"presets":[…]}`. Synapse removes the 16 built-in names and shows
  the rest as game presets (`007FirstLight_imm`, `ARCRaiders_cmm`, `ApexLegends_esp`, …; suffixes
  `_imm` / `_cmm` / `_esp` are per-game variants).
- Enhancement levels are passed as `(float)level`, **0–100 unscaled**. The UI sliders are 10…100.
  Internally they are the `State` fields `bass_boost(_enabled)`, `drc_enabled` / `drc_level`
  ("normalization" = dynamic range compression) and `dialog_enhancement(_enabled)` ("voice clarity").

### 4.2 EQ data
- `SetRenderEQGains` takes `JSON.stringify(number[10])`, integer dB, −12…+12, for
  31, 63, 125, 250, 500, 1k, 2k, 4k, 8k, 16k Hz (log: `renderEQData: [2,2,1,1,2,3,3,3,1,0]`).
- The DLL expands the 10 values to THX's **31-band** curve (1/3-octave, 20 Hz…16 kHz):
  `curve[0] = 0`, and `curve[1+3k] = curve[2+3k] = curve[3+3k] = g[k]` for k = 0…9. The stored state
  confirms it. Music is stored as `[0, 2,2,2, 2,2,2, 1,1,1, 1,1,1, 2,2,2, 3,3,3, 3,3,3, 3,3,3, 1,1,1, 0,0,0]`.
- The user EQ **replaces** the preset's own curve. THX's curated base curves at the 10 centre bands
  (Game Mode `3 3 1 0 0 1 2 3 4 4`, Cinema `3 3 3 1 3 1 1 1 3 4`, Music `0 0 0 0 0 1 1.5 2.5 3 4`)
  are overwritten as soon as Synapse sends its gains. Synapse's own preset gains are in
  [NARI_ULTIMATE.md](NARI_ULTIMATE.md) §5.
- Presets are stored per spatial flag: HKCU `UserState "<Preset>-<0|1>-Headphones-usb\1532\051A"`.

---

## 5. Mic tab

### 5.1 Microphone switch and volume
These are **Windows endpoint** settings (`isUseSystemMute = true`), not THX DSP:
- switch → `SetCaptureMuteBySystem(cid, !on)` = `IAudioEndpointVolume::SetMute` on the Chat capture endpoint;
- slider 0…100 → `SetCaptureVolumeBySystem(cid, v)` = `IAudioEndpointVolume::SetMasterVolumeLevelScalar(v / 100)`.

RazerAudioPC can do this directly with Core Audio, **no THX needed**.
- The log line `setCaptureVolumeByTHX(level: 75) raw: 75 converted: 10` is a different control:
  Synapse always sets the mic APO's own gain (param 5) to +10 dB. It maps 0…100 → −20…+20 dB, so
  75 → +10. This is not the user's slider.
- The headset's hardware mute button arrives as HID event `0x65`. Synapse mirrors it with `SetMute`
  (see HID doc §6).

### 5.2 Mic DSP
Every mic DSP export ends in `IVSSrvSettings::SetMicParams(index, float)` (verified by
disassembly; the indices match the mic APO's key table and the stored
`HKCU\SOFTWARE\VisiSonics\MicParams` blob `[0,200,0,-45,0,10,0,40,0,2,0,55,0,40,0,40,1,40]`):

| Index | Key | Export | Units / value here |
|---|---|---|---|
| 0 / 1 | hpf-on / hpf-freq | `SetCaptureHighpass` / `…HighpassLevel` | Hz (UI 0…100 → 75…400 Hz); 200 |
| 2 / 3 | gate-on / gate-thre | `SetCaptureVoiceGate` / `…VoiceGateLevel` | dB; −45 |
| 4 / 5 | mute / gain | `SetCaptureMute` / `SetCaptureVolume` | dB −20…+20; 10 |
| 6 / 7 | dre-on / dre-amt | `SetCaptureNoiseReduction` / `…NoiseReductionLevel` | 0…100; 40 |
| 8 / 9 | eq10-on / eq10-preset | `SetCaptureEQ` / `PresetCaptureEQ` | 0…3; 0 / 2 |
| 10 / 11 | clarity-on / clarity-amt | `SetCaptureVocalClarity` / `…VocalClarityLevel` | 0…100; 55 |
| 12 / 13 | de-esser on / amount | `SetCaptureParam` only | – |
| 14 / 15 | vol-norm-on / vol-norm-amt | `SetCaptureNormalization` / `…NormalizationLevel` | 0…100; 40 |
| 16 / 17 | limiter on / amount | `SetCaptureParam` only | 1 / 40 |

| UI control | Calls |
|---|---|
| Voice Gate | `SetCaptureVoiceGate(cid, on)`. Then `SetCaptureVoiceGateLevel(cid, dB)` where the UI step n = 1…5 → pct = 20·n → dB = −70 + pct/2, i.e. **−60, −50, −40, −30, −20 dB** |
| Mic Preview | ensure the capture device, then `SetCapturePreview(cid, on)` → `IVSSrvSettings::SetMicPreviewState`. The APO plays the processed mic to the default **communications** render device. Synapse forces it off at exit |
| Volume Normalization | params 14 / 15 (`SetCaptureNormalization`, `…Level` 0…100) |
| Vocal Clarity | params 10 / 11. On a level change Synapse sends off → level → on |
| Mic Noise Cancellation | params 6 / 7 (`SetCaptureNoiseReduction`, `…Level`) |
| Mic Equalizer | `SetCaptureEQGains(cid, "[10 dB values]")` → `IVSSrvSettings::SetMicEQGains(float[10])`. Bands (per the mw) 125, 250, 500, 750, 1k, 2k, 3k, 4k, 5k, 6k Hz; presets in [NARI_ULTIMATE.md](NARI_ULTIMATE.md) §5 |

- Synapse **never** calls `SetCaptureEQ(true)` or `PresetCaptureEQ` for the Nari, and the stored
  `eq10-on` is 0. Whether the mic EQ is audible without it is UNCERTAIN. Calling
  `SetCaptureEQ(cid, true)` alongside the gains is advisable.
- Synapse sends levels only when the feature is enabled and the value changed, mirroring the
  render enhancements.

### 5.3 Mic Monitoring (side tone) — the PC part
The side-tone level itself is a HID command (HID doc §4.4). Before sending it, Synapse calls
`SetStartCaptureStatus(cid, on)` and then ensures the capture device.
- `SetStartCaptureStatus(true)` (export at RVA 0x7F550 → `CRzAudioCapture::StartRecord`, verified by
  disassembly) opens a **dummy WASAPI capture stream** on the Nari microphone. The call sequence is
  `EnumAudioEndpoints(eCapture)`, `IAudioClient::Initialize` with the mix format,
  `IAudioCaptureClient`, and a reader thread that discards the data. `false` stops it.
- Reason (UNCERTAIN): the wireless mic path apparently carries audio only while the host is
  capturing, so the headset's side tone would be silent otherwise.
- RazerAudioPC can reproduce this with plain WASAPI (or NAudio's `WasapiCapture`) on the Chat
  capture endpoint while side tone is on. **No THX needed.**
- Note from Synapse's tooltip: with side tone on, the mic stays active and the headset won't enter
  power saving.

---

## 6. Driving VSSrv without Synapse

### 6.1 Mic side: COM `IVSSrvSettings` (typed, from VSSrv's embedded typelib "VSSrvLib" `{1FD49704-F52F-4A60-80C6-E22723DEEBE2}`)
CLSID `VSSrvSettings {087E4DB6-0519-4A63-9099-9201915E1371}`, IID `IVSSrvSettings {392375FF-8204-4A26-B4C5-AF4B71ACA729}`.
It derives from `IUnknown` and uses universal (typelib) marshalling. AppID
`{EF809770-D328-4410-9772-32775E710711}` has `LocalService = VSSrv`. All methods return `HRESULT`:

| vtbl | Method |
|---|---|
| +24 | `Init(UINT procId)` |
| +32 / +40 | `GetProcessingState(INT*)` / `SetProcessingState(INT)` |
| +48 / +56 / +64 / +72 | `GetHRTFState(INT*)` / `SetHRTFState(INT)` / `IsCustomHRTFPresent(INT*)` / `ReloadHRTF()` |
| +80 / +88 / +96 | `GetOutputDeviceParams` / `GetDefaultOutputDeviceParams` / `SetOutputDeviceParams(UINT version, UINT params[64], FLOAT data[64], UINT numParams)` |
| +104 / +112 | `GetOutputDevice(WCHAR[80])` / `SetOutputDevice(WCHAR[80] id, UINT deviceType)` |
| +120 / +128 | `GetInputDevice(WCHAR[80])` / `SetInputDevice(WCHAR[80] id)` |
| +136 … +160 | `Get/SetInputSidetoneState(INT)`, `Get/SetInputSidetoneLevel(FLOAT)` |
| +168 / +176 | `GetMicPreviewState(INT*)` / `SetMicPreviewState(INT)` |
| +184 / +192 | `GetMicEQGains(FLOAT[10])` / `SetMicEQGains(FLOAT[10])` |
| +200 / +208 | `GetMicParams(UINT index, FLOAT*)` / `SetMicParams(UINT index, FLOAT value)` (table §5.2) |

Expected sequence: `CoCreateInstance(CLSID_VSSrvSettings, CLSCTX_LOCAL_SERVER)` → `Init(GetCurrentProcessId())`
→ `SetInputDevice(<capture endpoint id>)` → `SetMicParams` / `SetMicEQGains` / `SetMicPreviewState`.
UNCERTAIN: VSSrv may reject unknown processes. It was not tested, because testing would mean calling a setter.

The typelib also defines the APO-facing interfaces (`IVSSrvTHXOutMFXAPO {D005A1D6-70C7-4B81-982B-97183D8B8B80}`
with `GetSystemState` / `NotifyAPOInit`; `IVSSrvInMFXAPO {03A6DA42-3D78-4E82-9E16-678D50EFCDC7}` with
`GetMicParams(FLOAT[64])`, `GetMicEQGains`, …) and the legacy render interface below.

### 6.2 Render side: ZeroMQ + protobuf (THX v3)
- Discovery: `HKLM\SOFTWARE\THX\Discovery`
  `thx:sa:service = tcp://127.0.0.1:<port>` (REQ/REP) and
  `thx:sa:status:publisher = tcp://127.0.0.1:<port>` (PUB, topics `thx:sa:state`,
  `thx:sa:status:settings_changed`, `thx:sa:status:error`). The ports are ephemeral, so read them
  on every start (here 49673 / 49672).
- Messages: `thx.sa.THXMessage { Any msg; string originator }`. Seen in the log:
  `[type.googleapis.com/thx.sa.Register] { pid: 15692 }`. The full recovered schema
  (`State`, `SetPreset`, `ResetPreset`, `StateChangeResult`, Preset/Room/Emitter services,
  notification events) is in [thx_spatial_audio_v3.proto](thx_spatial_audio_v3.proto). It parses and
  round-trips with protobufjs.
- The client library (`thxv3lib 3.2.0.0`, libzmq 4.3.5) appears to change settings by editing the
  current `State` and sending it, inside `BeginBatch` / `EndBatch` for EQ changes. It gets a
  `StateChangeResult` back. Frame headers seen in the DLL: `x-originator:`, `x-address:`,
  `x-payload:`, `x-Exception:`, `X-THX-CONN`. **The exact multipart framing is UNCERTAIN.** Capture
  a Synapse session on the loopback port before implementing it with NetMQ + Google.Protobuf.

### 6.3 Render side: legacy COM `IVSSrvTHXSettings`
CLSID `{2CCFC059-A1C5-4408-BCE5-0B23690DA7A2}`, IID `{3B3AF690-70A6-4DDA-BBEA-0B96493E9DA3}`.
Each setter takes an `LPSTR originator` and returns an in-band message buffer
(`UINT64* size, BYTE[4096]`):
`Init(procId)`, `Get/SetSpatialProcessingState`, `Get/SetProcessingMode`, `Get/SetCustomRoomType`,
`Get/SetCurrentModeEQGains(FLOAT[31])`, `ResetCurrentModeEQGains`, `Get/SetListeningMode`,
`Get/SetBassBoostState`, `Get/SetDialogEnhanceState`, `Get/SetDRCLevel(FLOAT)`, `Get/SetTiltEnabled`,
`Get/SetParam(UINT, FLOAT)`.
It has no preset-by-name and no bass-boost / dialog levels. UNCERTAIN whether its changes reach the
v3 APO. If they do, it is the easiest typed path for spatial on/off and the 31-band EQ.

### 6.4 Writing the registry or property store directly: not viable
The APOs pull settings from VSSrv over COM after a `Global\` event. The endpoint property
`{D5E8F0AB…},7` (a protobuf `thx.sa.State` blob) is only the out-APO's fallback at initialisation.
The HKCU keys are VSSrv's own persistence, and MMDevices writes need admin rights. Editing any of
them doesn't change live processing.

---

## 7. Options for RazerAudioPC

| Feature | No THX installed | THX package installed (Synapse removed) | Synapse's DLL available |
|---|---|---|---|
| Mic mute / volume | Core Audio `IAudioEndpointVolume` | same | same |
| Side tone keep-alive | WASAPI capture | same | `SetStartCaptureStatus` |
| Mic DSP (gate, NR, normalization, clarity, mic EQ, preview) | own capture APO or none | COM `IVSSrvSettings` (§6.1) | DLL exports |
| Spatial on/off, EQ, presets, bass boost, normalization, voice clarity | Equalizer APO / own APO (EQ, bass shelf, compressor). No THX spatial | ZeroMQ v3 (§6.2), or legacy COM (§6.3, UNCERTAIN) | DLL exports (§3) |

Recommendations:
1. **Start with what needs nothing extra**: the HID features, mic mute and volume (Core Audio), and
   the side-tone capture keep-alive. Together these cover the whole Power, Lighting and Enhancement
   (haptics) set, plus most of what people use on the Mic tab.
2. If `VSSrv` is installed (service present, `HKLM\SOFTWARE\THX\Discovery` exists), offer the THX
   features through VSSrv:
   - **COM first**, for the mic settings (typed, simplest);
   - **ZeroMQ** for render once the framing has been confirmed by a capture.
   Show the "THX not installed" state otherwise.
3. Loading `ThxV3Native.dll` from a Synapse install is the quickest way to prototype (§3), but it
   disappears with Synapse and can't be redistributed.
4. For PCs without THX, an Equalizer APO integration (write its `config.txt` with a 10-band
   `GraphicEQ` on the Nari Game endpoint) gives EQ and bass boost. It needs Equalizer APO installed
   on that endpoint and can conflict with the THX APO if both are present.
5. Make sure the Nari **Game** endpoint is the default output (console + communications) when the
   THX features are on. THX processes only VSSrv's current output device.

---

## 8. Open items (UNCERTAIN)
1. Exact ZeroMQ multipart framing for the v3 render API.
2. Whether a non-Razer process may use `IVSSrvSettings::Init` / `IVSSrvTHXSettings`.
3. Whether the mic EQ needs `SetCaptureEQ(true)`, and the `PresetCaptureEQ` index → preset order.
4. Who writes `{D5E8F0AB…}` property ids 6/7/11/12, and what 11/12 mean.
5. Whether the headset's side tone really needs the capture stream (mechanism verified, reason inferred).
6. Whether the legacy `audioBandPreset` table is used anywhere on the Nari.
