using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using System.Windows.Media;
using RazerAudioPC.Bluetooth;

namespace RazerAudioPC.Device;

public enum ConnectionState { Searching, Connecting, Connected }

public sealed record QuickConnectHost(string Name, byte[] Mac, bool Connected);

/// <summary>
/// State and commands for a Hammerhead Pro HyperSpeed (both buds). Everything here
/// runs on the UI thread; BLE events are marshalled back through the dispatcher.
/// </summary>
public sealed class HammerheadDevice : ObservableObject, IDisposable
{
    private readonly List<RazerChannel> _channels = new();
    private RazerChannel? _primary;
    private CancellationTokenSource? _cts;
    private DateTime _ignoreQuickSettingsUntil;

    // ---- connection ----
    private ConnectionState _state = ConnectionState.Searching;
    public ConnectionState State { get => _state; private set { if (Set(ref _state, value)) OnPropertyChanged(nameof(IsConnected)); } }
    public bool IsConnected => State == ConnectionState.Connected;

    private string _statusText = "Searching for your earbuds…";
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }

    /// <summary>Raised with a short message when a command fails (shown as a toast).</summary>
    public event Action<string>? Error;

    // ---- info ----
    private string _firmware = "—";
    public string Firmware { get => _firmware; private set => Set(ref _firmware, value); }
    private string _firmwareStatus = "";
    public string FirmwareStatus { get => _firmwareStatus; private set => Set(ref _firmwareStatus, value); }

    public async Task CheckFirmwareAsync()
    {
        FirmwareStatus = "Checking for updates…";
        var latest = await FirmwareCheck.LatestAsync();
        if (latest == null) FirmwareStatus = "Couldn't reach Razer's update server";
        else if (string.CompareOrdinal(latest.Version, Firmware) > 0)
            FirmwareStatus = $"Update available: {latest.Version}. Install it with the Razer Audio mobile app.";
        else FirmwareStatus = "Up to date";
    }

    private string _language = "—";
    public string Language { get => _language; private set => Set(ref _language, value); }
    public string Addresses => string.Join("  ", _channels.Select(c => $"{(c.IsLeft ? "L" : "R")}{(c.IsPrimary ? "*" : "")} {Discovery.Format(c.Address)}"));

    // ---- battery ----
    private int? _batteryLeft, _batteryRight;
    public int? BatteryLeft { get => _batteryLeft; private set => Set(ref _batteryLeft, value); }
    public int? BatteryRight { get => _batteryRight; private set => Set(ref _batteryRight, value); }
    private bool _charging;
    public bool Charging { get => _charging; private set => Set(ref _charging, value); }

    // ---- ANC ----
    private byte _anc;
    public byte Anc
    {
        get => _anc;
        private set
        {
            if (!Set(ref _anc, value)) return;
            if (value is > 0 and <= 10) AncLevel = value;
            OnPropertyChanged(nameof(AncDescription));
        }
    }
    private int _ancLevel = 10;
    public int AncLevel { get => _ancLevel; private set => Set(ref _ancLevel, value); }
    public string AncDescription => Anc switch { 0 => "Off", 0xFF => "Ambient", _ => $"On · Level {Anc}" };

    // ---- EQ ----
    private EqPreset _eqPreset;
    public EqPreset EqPreset { get => _eqPreset; private set { if (Set(ref _eqPreset, value)) OnPropertyChanged(nameof(EqDescription)); } }
    public string EqDescription => Presets.Name(EqPreset);
    private byte[] _eqBands = Enumerable.Repeat((byte)Presets.BandFlat, Presets.BandCount).ToArray();
    public byte[] EqBands { get => _eqBands; private set => Set(ref _eqBands, value); }

    // ---- toggles ----
    private bool _gameMode;
    public bool GameMode { get => _gameMode; private set => Set(ref _gameMode, value); }
    private bool _doNotDisturb;
    public bool DoNotDisturb { get => _doNotDisturb; private set => Set(ref _doNotDisturb, value); }
    private bool _autoPause;
    public bool AutoPause { get => _autoPause; private set => Set(ref _autoPause, value); }
    private bool _lowPowerSupported;
    public bool LowPowerSupported { get => _lowPowerSupported; private set => Set(ref _lowPowerSupported, value); }
    private bool _lowPower;
    public bool LowPower { get => _lowPower; private set => Set(ref _lowPower, value); }
    private int _autoPowerOff;
    public int AutoPowerOff { get => _autoPowerOff; private set { if (Set(ref _autoPowerOff, value)) OnPropertyChanged(nameof(AutoPowerOffDescription)); } }
    public string AutoPowerOffDescription => AutoPowerOff == 0 ? "Never" : AutoPowerOff == 60 ? "After 1 hour" : $"After {AutoPowerOff} minutes";

    // ---- wear / fit ----
    private bool _wornLeft, _wornRight;
    public bool WornLeft { get => _wornLeft; private set { if (Set(ref _wornLeft, value)) OnPropertyChanged(nameof(AnyWorn)); } }
    public bool WornRight { get => _wornRight; private set { if (Set(ref _wornRight, value)) OnPropertyChanged(nameof(AnyWorn)); } }
    public bool AnyWorn => WornLeft || WornRight;

    // ---- chroma ----
    private ChromaEffect _chromaEffect = ChromaEffect.Static;
    public ChromaEffect ChromaEffect
    {
        get => _chromaEffect;
        private set
        {
            if (!Set(ref _chromaEffect, value)) return;
            OnPropertyChanged(nameof(ChromaOn));
            OnPropertyChanged(nameof(ChromaDescription));
        }
    }
    public bool ChromaOn => ChromaEffect != ChromaEffect.Off;
    public string ChromaDescription => ChromaEffect switch
    {
        ChromaEffect.Off => "Off",
        ChromaEffect.Static => "Static",
        ChromaEffect.Breathing => "Breathing",
        ChromaEffect.AudioMeter => "Audio Meter",
        _ => "Spectrum Cycling",
    };
    private Color _chromaColor = Color.FromRgb(0x44, 0xD6, 0x2C);
    public Color ChromaColor { get => _chromaColor; private set => Set(ref _chromaColor, value); }
    private int _chromaBrightness = 255;
    public int ChromaBrightness { get => _chromaBrightness; private set => Set(ref _chromaBrightness, value); }

    // ---- quick connect ----
    public ObservableCollection<QuickConnectHost> QuickConnectHosts { get; } = new();
    private string _quickConnectDescription = "No known audio source";
    public string QuickConnectDescription { get => _quickConnectDescription; private set => Set(ref _quickConnectDescription, value); }

    // ---- touch maps (per bud) ----
    private TouchMap? _touchLeft, _touchRight;
    public TouchMap? TouchLeft { get => _touchLeft; private set => Set(ref _touchLeft, value); }
    public TouchMap? TouchRight { get => _touchRight; private set => Set(ref _touchRight, value); }

    // ===================================================================== connection

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _ = RunAsync(_cts.Token);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (!IsConnected)
            {
                try
                {
                    await ConnectOnceAsync(ct);
                }
                catch (Exception ex)
                {
                    BleLog.Write($"[DEVICE] connect error: {ex}");
                }
                if (!IsConnected)
                {
                    await DelaySafe(TimeSpan.FromSeconds(5), ct);
                    continue;
                }
            }

            // Periodic battery refresh while connected.
            await DelaySafe(TimeSpan.FromSeconds(60), ct);
            if (IsConnected) await Try(RefreshBatteryAsync, null);
        }
    }

    private static async Task DelaySafe(TimeSpan t, CancellationToken ct)
    {
        try { await Task.Delay(t, ct); } catch (OperationCanceledException) { }
    }

    private async Task ConnectOnceAsync(CancellationToken ct)
    {
        State = ConnectionState.Searching;
        StatusText = "Searching for your earbuds…";
        var candidates = await Discovery.FindAsync(TimeSpan.FromSeconds(6), ct);
        if (candidates.Count == 0)
        {
            StatusText = "Earbuds not found. Take them out of the case and make sure they're on.";
            return;
        }

        State = ConnectionState.Connecting;
        StatusText = "Connecting…";
        DisposeChannels();

        foreach (var c in candidates)
        {
            if (_channels.Count >= 2 || ct.IsCancellationRequested) break;
            var ch = new RazerChannel();
            if (!await ch.ConnectAsync(c.Address))
            {
                ch.Dispose();
                continue;
            }
            try
            {
                var grs = await ch.GetAsync(Protocol.Grs, 1000, 4);
                ch.Grs = grs[0];
            }
            catch (Exception ex)
            {
                BleLog.Write($"[DEVICE] GRS failed on {Discovery.Format(c.Address)}: {ex.Message}");
                ch.Dispose();
                continue;
            }
            if (_channels.Any(o => o.IsLeft == ch.IsLeft))
            {
                ch.Dispose();
                continue;
            }
            ch.EventReceived += (s, d) => Application.Current.Dispatcher.BeginInvoke(() => OnEvent(s, d));
            ch.Disconnected += s => Application.Current.Dispatcher.BeginInvoke(() => OnChannelLost(s));
            _channels.Add(ch);
            BleLog.Write($"[DEVICE] {Discovery.Format(c.Address)} grs={ch.Grs:X2} left={ch.IsLeft} primary={ch.IsPrimary}");
        }

        _primary = _channels.FirstOrDefault(c => c.IsPrimary) ?? _channels.FirstOrDefault();
        if (_primary == null)
        {
            StatusText = "Couldn't open the control channel. Close the Razer Audio app on your phone and retry.";
            return;
        }

        StatusText = "Reading settings…";
        await InitializeAsync();
        OnPropertyChanged(nameof(Addresses));
        State = ConnectionState.Connected;
        StatusText = "Connected";
        _ = CheckFirmwareAsync();
    }

    /// <summary>Same read order as HammerheadT1V2.updateFromDevice (§4.2); every step is non-fatal here.</summary>
    private async Task InitializeAsync()
    {
        var p = _primary!;
        await Try(async () => ApplyQuickSettings((await p.GetAsync(Protocol.QuickSettings, 200, 2))[0]), null);
        await Try(async () => Anc = (await p.GetAsync(Protocol.Anc, 500, 2))[0], null);
        await Try(async () => AutoPause = (await p.GetAsync(Protocol.AutoPause, 500, 2))[0] == 1, null);
        foreach (var ch in _channels) await Try(() => ReadTouchMapAsync(ch), null);
        await Try(async () =>
        {
            var fw = await p.GetAsync(Protocol.FirmwareVersion, 400, 5);
            Firmware = string.Join(".", fw.Take(4).Select(b => b.ToString("00")));
            Language = Presets.LanguageName(fw[^1]);
            LowPowerSupported = string.CompareOrdinal(Firmware, Protocol.LowPowerMinFirmware) >= 0;
        }, null);
        await Try(RefreshBatteryAsync, null);
        await Try(async () => EqPreset = (EqPreset)(await p.GetAsync(Protocol.EqPreset, 500, 2))[0], null);
        await Try(ReadEqBandsAsync, null);
        await Try(ReadQuickConnectHostsAsync, null);
        await Try(async () => AutoPowerOff = (await p.GetAsync(Protocol.AutoPowerOff, 500, 2))[0], null);
        await Try(async () => DoNotDisturb = (await p.GetAsync(Protocol.DoNotDisturb, 100, 3))[0] == 1, null);
        await Try(ReadChromaAsync, null);
        await Try(ReadWearAsync, null);
        if (LowPowerSupported)
            await Try(async () => LowPower = (await p.GetAsync(Protocol.LowPowerMode, 500, 3))[0] == 1, null);
    }

    private void OnChannelLost(RazerChannel ch)
    {
        if (!_channels.Contains(ch) || !IsConnected) return;
        BleLog.Write($"[DEVICE] lost {Discovery.Format(ch.Address)}");
        if (ch == _primary || _channels.All(c => !c.IsConnected))
        {
            State = ConnectionState.Searching;
            StatusText = "Connection lost. Reconnecting…";
            BatteryLeft = BatteryRight = null;
        }
    }

    /// <summary>`--demo`: fills the model with sample values so the UI can be previewed without earbuds.</summary>
    public void LoadDemoState()
    {
        State = ConnectionState.Connected;
        StatusText = "Demo";
        Firmware = "01.04.04.03";
        Language = "English";
        LowPowerSupported = true;
        BatteryLeft = 100;
        BatteryRight = 100;
        Anc = 7;
        EqPreset = EqPreset.Custom;
        EqBands = new byte[] { 10, 9, 7, 6, 3, 2, 3, 6, 8, 10 };
        GameMode = false;
        AutoPause = true;
        WornLeft = WornRight = true;
        ChromaEffect = ChromaEffect.Static;
        TouchLeft = TouchMap.Default(true);
        TouchRight = TouchMap.Default(false);
        QuickConnectHosts.Add(new QuickConnectHost("DESKTOP-PC", new byte[6], true));
        QuickConnectHosts.Add(new QuickConnectHost("Pixel 9", new byte[6], false));
        QuickConnectDescription = "DESKTOP-PC";
    }

    public void Reconnect()
    {
        _cts?.Cancel();
        DisposeChannels();
        State = ConnectionState.Searching;
        Start();
    }

    private void DisposeChannels()
    {
        foreach (var c in _channels) c.Dispose();
        _channels.Clear();
        _primary = null;
    }

    public void Dispose()
    {
        _cts?.Cancel();
        DisposeChannels();
    }

    // ===================================================================== events (§3.17)

    private void OnEvent(RazerChannel source, byte[] d)
    {
        if (d.Length < 4) return;
        switch (d[0])
        {
            case Protocol.Anc:
                Anc = d[3];
                break;
            case Protocol.Battery when d.Length >= 5:
                ApplyBattery(d[3..]);
                break;
            case Protocol.ChargingEvent:
                Charging = d[3] == 1;
                _ = Try(RefreshBatteryAsync, null);
                break;
            case Protocol.QuickSettings when DateTime.UtcNow > _ignoreQuickSettingsUntil:
                ApplyQuickSettings(d[3]);
                break;
            case Protocol.QuickConnectHosts:
                _ = Try(ReadQuickConnectHostsAsync, null);
                break;
            case Protocol.WearStatus when d.Length >= 5:
                WornLeft = d[3] > 0;
                WornRight = d[4] > 0;
                break;
            case Protocol.FitStatus when d.Length >= 5:
                FitResult?.Invoke(d[3] > 0, d[4] > 0);
                break;
            case Protocol.LowPowerMode:
            case Protocol.LowPowerMode | Protocol.Set:
                LowPower = d[3] == 1;
                break;
        }
    }

    // ===================================================================== reads

    private void ApplyQuickSettings(byte v) => GameMode = v == (byte)QuickSetting.GameModeOn;

    private async Task RefreshBatteryAsync()
    {
        if (_primary == null) return;
        ApplyBattery(await _primary.GetAsync(Protocol.Battery, 500, 3));
    }

    /// <summary>T1V2: payload[0] = right, payload[1] = left (§3.3). 0xFF = bud unavailable.</summary>
    private void ApplyBattery(byte[] b)
    {
        if (b.Length < 2) return;
        BatteryRight = b[0] <= 100 ? b[0] : null;
        BatteryLeft = b[1] <= 100 ? b[1] : null;
    }

    private async Task ReadEqBandsAsync()
    {
        var bands = await _primary!.GetAsync(Protocol.EqBands, 300, 2);
        if (bands.Length >= Presets.BandCount) EqBands = bands[..Presets.BandCount];
    }

    private async Task ReadTouchMapAsync(RazerChannel ch)
    {
        var data = await ch.GetAsync(Protocol.TouchMap, 1000, 2);
        var map = new TouchMap(data);
        if (ch.IsLeft) TouchLeft = map; else TouchRight = map;
    }

    private async Task ReadWearAsync()
    {
        var w = await _primary!.GetAsync(Protocol.WearStatus, 500, 2);
        if (w.Length < 2) return;
        WornLeft = w[0] > 0;
        WornRight = w[1] > 0;
    }

    private async Task ReadChromaAsync()
    {
        var e = await _primary!.GetAsync(Protocol.ChromaEffect, 300, 3);
        if (e.Length > 0)
        {
            ChromaEffect = e[0] switch
            {
                0 => ChromaEffect.Off,
                2 => ChromaEffect.Breathing,
                3 => ChromaEffect.Static,
                4 => ChromaEffect.AudioMeter,
                _ => ChromaEffect.Spectrum,
            };
            if (e.Length >= 4) ChromaColor = Color.FromRgb(e[1], e[2], e[3]);
        }
        var b = await _primary.GetAsync(Protocol.ChromaBrightness, 300, 2);
        if (b.Length > 0) ChromaBrightness = b[0];
    }

    private async Task ReadQuickConnectHostsAsync()
    {
        var resp = await _primary!.GetAsync(Protocol.QuickConnectHosts, 500, 2);
        QuickConnectHosts.Clear();
        int i = 0;
        // Records: [recLen][connected][mac×6][name × (recLen-7)]
        while (i + 8 <= resp.Length)
        {
            int recLen = resp[i];
            bool connected = resp[i + 1] > 0;
            var mac = resp[(i + 2)..(i + 8)];
            int nameLen = Math.Max(0, recLen - 7);
            int end = Math.Min(resp.Length, i + 8 + nameLen);
            string name = Encoding.UTF8.GetString(resp, i + 8, end - (i + 8)).TrimEnd('\0');
            QuickConnectHosts.Add(new QuickConnectHost(name, mac, connected));
            i = end;
            if (recLen == 0) break;
        }
        var current = QuickConnectHosts.FirstOrDefault(h => h.Connected);
        QuickConnectDescription = current?.Name ?? (QuickConnectHosts.Count == 0 ? "No known audio source" : "Not connected");
    }

    // ===================================================================== commands

    private async Task<bool> Try(Func<Task> action, string? failure)
    {
        try
        {
            await action();
            return true;
        }
        catch (Exception ex)
        {
            BleLog.Write($"[DEVICE] {failure ?? "read"} failed: {ex.Message}");
            if (failure != null) Error?.Invoke(failure);
            return false;
        }
    }

    private RazerChannel Primary => _primary ?? throw new InvalidOperationException("Not connected");

    public Task SetAncAsync(byte value) => Try(async () =>
    {
        await Primary.SetAsync(Protocol.Anc | Protocol.Set, new[] { value }, 300, 3);
        Anc = value;
        if (value is > 0 and <= 10 && LowPower) LowPower = false; // the app forces low power off with ANC
    }, "Couldn't change noise cancellation");

    public Task SetEqPresetAsync(EqPreset preset) => Try(async () =>
    {
        await Primary.SetAsync(Protocol.EqPreset | Protocol.Set, new[] { (byte)preset }, 500, 2);
        EqPreset = preset;
        if (preset == EqPreset.Custom) await ReadEqBandsAsync();
    }, "Couldn't change the equalizer preset");

    public Task SetEqBandsAsync(byte[] bands) => Try(async () =>
    {
        var clamped = bands.Select(b => (byte)Math.Clamp((int)b, 0, Presets.BandMax)).ToArray();
        var r = await Primary.SetAsync(Protocol.EqBands | Protocol.Set, clamped, 300, 3);
        if (r.Length > 0 && r[0] != 0) throw new InvalidOperationException($"result {r[0]}");
        EqBands = clamped;
    }, "Couldn't update the custom equalizer");

    public Task SetGameModeAsync(bool on) => Try(async () =>
    {
        _ignoreQuickSettingsUntil = DateTime.UtcNow.AddMilliseconds(500);
        var v = on ? QuickSetting.GameModeOn : QuickSetting.QuickConnectOn;
        await Primary.SetAsync(Protocol.QuickSettings | Protocol.Set, new[] { (byte)v }, 300, 2);
        GameMode = on;
    }, "Couldn't change Game Mode");

    public Task SetDoNotDisturbAsync(bool on) => Try(async () =>
    {
        await Primary.SetAsync(Protocol.DoNotDisturb | Protocol.Set, new[] { (byte)(on ? 1 : 0) }, 1000, 3);
        DoNotDisturb = on;
    }, "Couldn't change Do Not Disturb");

    public Task SetAutoPauseAsync(bool on) => Try(async () =>
    {
        // Sent exactly as the official app does: length byte 0 followed by the value (§3.7).
        await Primary.SendAsync(new byte[] { Protocol.AutoPause | Protocol.Set, 0, 0, (byte)(on ? 1 : 0) }, 500, 2);
        AutoPause = (await Primary.GetAsync(Protocol.AutoPause, 500, 2))[0] == 1;
    }, "Couldn't change Auto Pause");

    public Task SetLowPowerAsync(bool on) => Try(async () =>
    {
        await Primary.SetAsync(Protocol.LowPowerMode | Protocol.Set, new[] { (byte)(on ? 1 : 0) }, 500, 2);
        LowPower = on;
    }, "Couldn't change Low Power Mode");

    public Task SetAutoPowerOffAsync(int minutes) => Try(async () =>
    {
        await Primary.SetAsync(Protocol.AutoPowerOff | Protocol.Set, new[] { (byte)minutes }, 600, 2);
        AutoPowerOff = (await Primary.GetAsync(Protocol.AutoPowerOff, 500, 2))[0];
    }, "Couldn't change Auto Standby");

    public Task SetChromaAsync(ChromaEffect effect, Color color) => Try(async () =>
    {
        byte[] payload = effect switch
        {
            ChromaEffect.Static or ChromaEffect.Breathing or ChromaEffect.AudioMeter =>
                new[] { (byte)effect, color.R, color.G, color.B },
            _ => new[] { (byte)effect },
        };
        var r = await Primary.SetAsync(Protocol.ChromaEffect | Protocol.Set, payload, 3000, 2);
        if (r.Length > 0 && r[0] != 0)
        {
            await Task.Delay(100);
            await Primary.SetAsync(Protocol.ChromaEffect | Protocol.Set, payload, 3000, 1);
        }
        if (effect == ChromaEffect.Restore) await ReadChromaAsync();
        else
        {
            ChromaEffect = effect;
            ChromaColor = color;
        }
    }, "Couldn't change the lighting");

    public Task SetChromaBrightnessAsync(int value) => Try(async () =>
    {
        await Primary.SetAsync(Protocol.ChromaBrightness | Protocol.Set, new[] { (byte)Math.Clamp(value, 0, 255) }, 300, 3);
        ChromaBrightness = value;
    }, "Couldn't change brightness");

    public Task ConnectQuickConnectHostAsync(QuickConnectHost host) => Try(async () =>
    {
        await Primary.SetAsync(Protocol.QuickConnectHosts | Protocol.Set, host.Mac, 1000, 2);
        await Task.Delay(1500);
        await ReadQuickConnectHostsAsync();
    }, "Couldn't switch audio source");

    public Task<bool> WriteTouchMapAsync(bool left, TouchMap map) => Try(async () =>
    {
        var ch = _channels.FirstOrDefault(c => c.IsLeft == left)
                 ?? throw new InvalidOperationException($"The {(left ? "left" : "right")} earbud isn't connected");
        var r = await ch.SetAsync(Protocol.TouchMap | Protocol.Set, map.ToArray(), 1000, 2);
        if (r.Length > 0 && r[0] != 0) throw new InvalidOperationException($"result {r[0]}");
        await ReadTouchMapAsync(ch);
    }, "Couldn't save the gesture");

    public bool HasBud(bool left) => _channels.Any(c => c.IsLeft == left);

    // ---- fit test (§3.13) ----

    public event Action<bool, bool>? FitResult;

    /// <summary>Starts the fit test, waits for the tone to finish and returns (leftGood, rightGood).</summary>
    public async Task<(bool Left, bool Right)?> RunFitTestAsync()
    {
        (bool, bool)? last = null;
        void Handler(bool l, bool r) => last = (l, r);
        FitResult += Handler;
        try
        {
            var r = await Primary.SetAsync(Protocol.FitTestStart, Array.Empty<byte>(), 1000, 2);
            if (r.Length > 0 && r[0] == 0) throw new InvalidOperationException("Fit test refused");
            await Task.Delay(8600);
            if (last == null)
            {
                var f = await Primary.GetAsync(Protocol.FitStatus, 500, 2);
                if (f.Length >= 2) last = (f[0] > 0, f[1] > 0);
            }
            return last;
        }
        catch (Exception ex)
        {
            BleLog.Write($"[DEVICE] fit test failed: {ex.Message}");
            Error?.Invoke("The fit test couldn't run. Put both earbuds in and try again.");
            try { await Primary.SetAsync(Protocol.FitTestCancel, Array.Empty<byte>(), 1000, 1); } catch { }
            return null;
        }
        finally
        {
            FitResult -= Handler;
        }
    }
}
