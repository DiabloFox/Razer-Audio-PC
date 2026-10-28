# Razer Audio PC

A Windows app to control the **Razer Hammerhead Pro HyperSpeed** earbuds without the Razer Audio mobile app.
It uses the same Bluetooth LE control protocol as the official app (documented in [docs/PROTOCOL.md](docs/PROTOCOL.md)).

## Features

| Feature | Notes |
|---|---|
| Battery (left / right, charging) | Also in the tray tooltip, with a low-battery alert |
| Equalizer | Default (THX), Amplified, Vocal, Enhanced Bass, Enhanced Clarity, Custom 10-band (31 Hz – 16 kHz) |
| Active Noise Cancellation | Off / On (level 1–10) / Ambient, plus the earbud fit test |
| Remap gestures | Per ear, media and call actions, reset to defaults |
| Chroma lighting | Off, Static, Breathing, Spectrum Cycling, Audio Meter; colour and brightness |
| Game Mode, Do Not Disturb, Auto Pause / Resume | Switches on the dashboard |
| Low Power Mode | Only on firmware 01.04.04.03 or newer |
| Quick Connect | Switch audio between remembered hosts |
| Auto Standby | Auto power-off timer (the firmware supports it; the mobile app hides it) |
| Tray icon | Quick menu for ANC, EQ, Game Mode and lighting; start with Windows |

## Build and run

Requires Windows 10 2004+ and the .NET 9 SDK.

```
dotnet build -c Release
src\RazerAudioPC\bin\Release\net9.0-windows10.0.19041.0\RazerAudioPC.exe
```

Command-line switches:

* `--minimized`: start in the tray (used by "Start with Windows")
* `--demo`: preview the UI with sample data, without earbuds
* `--probe`: read-only connection diagnostic; writes `probe.log` next to the exe

The app log is at `%AppData%\RazerAudioPC\log.txt` (Settings → Open log).

## Connecting

The earbuds pair with Windows for audio over Classic Bluetooth. The app's controls use a separate Bluetooth LE
link to each earbud (the Razer app does the same). Only one device can hold that LE link at a time, so:

1. Close the Razer Audio app on your phone, or turn off Bluetooth on the phone.
2. Take both earbuds out of the case.
3. Open the app. It finds the earbuds automatically, using the address Windows already knows.
