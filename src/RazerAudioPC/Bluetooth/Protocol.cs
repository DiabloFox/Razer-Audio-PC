namespace RazerAudioPC.Bluetooth;

/// <summary>Hammerhead Pro HyperSpeed BLE constants — see docs/PROTOCOL.md.</summary>
public static class Protocol
{
    public static readonly Guid ServiceUuid = Guid.Parse("0000fd65-0000-1000-8000-00805f9b34fb");
    public static readonly Guid WriteUuid = Guid.Parse("416d0000-2d52-617a-6572-424c4501f40a");
    public static readonly Guid NotifyUuid = Guid.Parse("416d0001-2d52-617a-6572-424c4501f40a");

    public const ushort RazerCompanyId = 0x068E;
    public const byte ProductType = 0x08;
    public const byte ModelId = 0x15;

    // GET ids; SET = GET | 0x80 (§3.0)
    public const byte FirmwareVersion = 0x02;
    public const byte Anc = 0x12;
    public const byte EqPreset = 0x13;
    public const byte EqBands = 0x15;
    public const byte Battery = 0x21;
    public const byte AutoPause = 0x22;
    public const byte TouchMap = 0x23;
    public const byte DoNotDisturb = 0x27;
    public const byte ChargingEvent = 0x2A;
    public const byte QuickSettings = 0x2B;
    public const byte AutoPowerOff = 0x2C;
    public const byte QuickConnectHosts = 0x2D;
    public const byte LowPowerMode = 0x2F;
    public const byte Grs = 0x32;
    public const byte ChromaEffect = 0x40;
    public const byte ChromaBrightness = 0x41;
    public const byte WearStatus = 0x50;
    public const byte FitStatus = 0x51;

    public const byte Set = 0x80;
    public const byte FitTestStart = 0xD0;
    public const byte FitTestCancel = 0xD1;

    public const string LowPowerMinFirmware = "01.04.04.03";
}

public enum AncMode : byte { Off = 0x00, Ambient = 0xFF }

public enum EqPreset : byte
{
    DefaultThx = 0x00,
    Amplified = 0x01,
    Vocal = 0x02,
    EnhancedBass = 0x03,
    EnhancedClarity = 0x04,
    Custom = 0xFF,
}

public enum QuickSetting : byte { Off = 0x00, GameModeOn = 0x01, QuickConnectOn = 0x02 }

public enum ChromaEffect : byte
{
    Off = 0x00,
    Spectrum = 0x01,
    Breathing = 0x02,
    Static = 0x03,
    AudioMeter = 0x04,
    Restore = 0xFF,
}

public enum StreamFunction : byte
{
    PlayPause = 0x00,
    Stop = 0x01,
    VoiceAssistant = 0x02,
    NextTrack = 0x03,
    PreviousTrack = 0x04,
    Anc = 0x05,
    GameMode = 0x07,
    Pairing = 0x08,
    VolumeUp = 0x09,
    VolumeDown = 0x0A,
    SmartSwitch = 0x0B,
    LowPower = 0x0C,
    Disable = 0xFE,
    NotAvailable = 0xFF,
}

public enum CallFunction : byte
{
    Answer = 0x00,
    Reject = 0x01,
    EndCall = 0x02,
    AnswerAndSwitch = 0x03,
    SwitchCall = 0x04,
    VolumeUp = 0x05,
    VolumeDown = 0x06,
    Disable = 0xFE,
    NotAvailable = 0xFF,
}

/// <summary>Gesture columns of the V3 touch map (§3.12). Value = 0-based column index.</summary>
public enum Gesture
{
    SinglePress = 0,
    DoubleTap = 1,
    TripleTap = 2,
    TripleTapHold = 3,
    Hold2Sec = 4,
    Hold4Sec = 5,
    DoubleTapHold = 6,
}

public static class Presets
{
    public static readonly string[] BandLabels = { "31", "63", "125", "250", "500", "1K", "2K", "4K", "8K", "16K" };
    public const int BandCount = 10;
    public const int BandFlat = 5;
    public const int BandMax = 10;

    /// <summary>Reference curves the app draws for presets (display units, −5..+5).</summary>
    public static double[] CurveFor(EqPreset p) => p switch
    {
        EqPreset.Amplified => new double[] { -2, 2, 3, 4, 5, 5, 5, 4, 2, -2 },
        EqPreset.Vocal => new double[] { 3, 3, 2, 1, -2, -2, -2, 1, 2, 4 },
        EqPreset.EnhancedBass => new double[] { 5, 5, 5, 2, 0, 0, 0, 2, 2, 2 },
        EqPreset.EnhancedClarity => new double[] { -5, -4, -2, -1, 0, 0, 0, 2, 2, 2 },
        _ => new double[10],
    };

    public static string Name(EqPreset p) => p switch
    {
        EqPreset.DefaultThx => "Default (THX)",
        EqPreset.Amplified => "Amplified",
        EqPreset.Vocal => "Vocal",
        EqPreset.EnhancedBass => "Enhanced Bass",
        EqPreset.EnhancedClarity => "Enhanced Clarity",
        EqPreset.Custom => "Custom",
        _ => "Unknown",
    };

    public static string LanguageName(byte v) => v switch
    {
        0 => "English",
        1 => "简体中文",
        2 => "Français",
        3 => "Deutsch",
        4 => "日本語",
        5 => "한국어",
        6 => "Español",
        _ => "Unknown",
    };
}
