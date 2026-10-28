using RazerAudioPC.Bluetooth;

namespace RazerAudioPC.Device;

/// <summary>
/// V3 touch map (docs/PROTOCOL.md §3.12): 6 rows × 7 gesture columns.
/// Rows A–D are call contexts (CallFunction), E = streaming, F = idle (StreamFunction).
/// </summary>
public sealed class TouchMap
{
    public const int Columns = 7;
    public const int Length = 42;

    private const int RowA = 0, RowB = 1, RowC = 2, RowD = 3, RowE = 4, RowF = 5;

    private readonly byte[] _data;

    public TouchMap(byte[] data)
    {
        if (data.Length < Length) throw new ArgumentException($"Touch map needs {Length} bytes, got {data.Length}");
        _data = data[..Length];
    }

    public byte[] ToArray() => (byte[])_data.Clone();

    private byte this[int row, Gesture g]
    {
        get => _data[row * Columns + (int)g];
        set => _data[row * Columns + (int)g] = value;
    }

    /// <summary>Displayed stream function = row E.</summary>
    public StreamFunction GetStream(Gesture g) => (StreamFunction)this[RowE, g];

    /// <summary>Displayed call function = first non-DISABLE of rows A..D.</summary>
    public CallFunction GetCall(Gesture g)
    {
        foreach (int row in new[] { RowA, RowB, RowC, RowD })
        {
            var f = (CallFunction)this[row, g];
            if (f != CallFunction.Disable) return f;
        }
        return CallFunction.Disable;
    }

    public void SetStream(Gesture g, StreamFunction f)
    {
        this[RowE, g] = (byte)f;
        // Volume only applies while streaming; idle row gets "do nothing".
        this[RowF, g] = f is StreamFunction.VolumeUp or StreamFunction.VolumeDown ? (byte)StreamFunction.Disable : (byte)f;
    }

    public void SetCall(Gesture g, CallFunction f)
    {
        const byte D = (byte)CallFunction.Disable;
        (byte a, byte b, byte c, byte d) = f switch
        {
            CallFunction.Answer or CallFunction.EndCall =>
                ((byte)CallFunction.Answer, (byte)CallFunction.EndCall, D, D),
            CallFunction.AnswerAndSwitch =>
                (D, D, (byte)CallFunction.AnswerAndSwitch, (byte)CallFunction.SwitchCall),
            CallFunction.Reject =>
                ((byte)CallFunction.Reject, D, (byte)CallFunction.Reject, D),
            _ => ((byte)f, (byte)f, (byte)f, (byte)f),
        };
        this[RowA, g] = a;
        this[RowB, g] = b;
        this[RowC, g] = c;
        this[RowD, g] = d;
    }

    public static TouchMap Default(bool left) => new(Convert.FromHexString(left
        ? "00FEFEFE01FE06" + "02FEFEFEFEFE06" + "FE03FEFE01FE06" + "FE04FEFE02FE06" + "00030B0705080A" + "00FE0B070508FE"
        : "00FEFEFE01FE05" + "02FEFEFEFEFE05" + "FE03FEFE01FE05" + "FE04FEFE02FE05" + "00030B07050809" + "00FE0B070508FE"));

    // ---- UI metadata ----

    /// <summary>Remappable gestures in display order (triple tap is fixed to Smart Switch; hold 4 s is pairing).</summary>
    public static readonly (Gesture Gesture, string Label)[] EditableGestures =
    {
        (Gesture.SinglePress, "SINGLE PRESS"),
        (Gesture.DoubleTap, "DOUBLE TAP"),
        (Gesture.Hold2Sec, "HOLD 2 SEC."),
        (Gesture.TripleTapHold, "TRIPLE TAP HOLD 2 SEC."),
        (Gesture.DoubleTapHold, "DOUBLE TAP AND HOLD"),
    };

    public static StreamFunction[] StreamOptions(Gesture g) => g == Gesture.DoubleTapHold
        ? new[] { StreamFunction.VolumeUp, StreamFunction.VolumeDown, StreamFunction.Disable }
        : new[]
        {
            StreamFunction.PlayPause, StreamFunction.NextTrack, StreamFunction.PreviousTrack, StreamFunction.Stop,
            StreamFunction.VoiceAssistant, StreamFunction.Anc, StreamFunction.GameMode, StreamFunction.Disable,
        };

    public static CallFunction[] CallOptions(Gesture g) => g == Gesture.DoubleTapHold
        ? new[] { CallFunction.VolumeUp, CallFunction.VolumeDown, CallFunction.Disable }
        : new[] { CallFunction.Answer, CallFunction.AnswerAndSwitch, CallFunction.Reject, CallFunction.Disable };

    public static string Label(StreamFunction f) => f switch
    {
        StreamFunction.PlayPause => "PLAY / PAUSE",
        StreamFunction.Stop => "STOP",
        StreamFunction.VoiceAssistant => "VOICE ASSISTANT",
        StreamFunction.NextTrack => "NEXT TRACK",
        StreamFunction.PreviousTrack => "PREVIOUS TRACK",
        StreamFunction.Anc => "ANC / AMBIENT",
        StreamFunction.GameMode => "GAME MODE",
        StreamFunction.Pairing => "BLUETOOTH PAIR",
        StreamFunction.VolumeUp => "VOLUME UP",
        StreamFunction.VolumeDown => "VOLUME DOWN",
        StreamFunction.SmartSwitch => "SMART SWITCH",
        StreamFunction.LowPower => "LOW POWER MODE",
        StreamFunction.Disable => "DO NOTHING",
        _ => "NOT AVAILABLE",
    };

    public static string Label(CallFunction f) => f switch
    {
        CallFunction.Answer or CallFunction.EndCall => "PICK UP / HANG UP",
        CallFunction.AnswerAndSwitch or CallFunction.SwitchCall => "ANSWER / SWITCH TO NEW CALL",
        CallFunction.Reject => "REJECT CALL",
        CallFunction.VolumeUp => "VOLUME UP",
        CallFunction.VolumeDown => "VOLUME DOWN",
        CallFunction.Disable => "DO NOTHING",
        _ => "NOT AVAILABLE",
    };

    /// <summary>Segoe Fluent glyph for each function (bottom-sheet icons).</summary>
    public static string Glyph(StreamFunction f) => f switch
    {
        StreamFunction.PlayPause => "",
        StreamFunction.Stop => "",
        StreamFunction.VoiceAssistant => "",
        StreamFunction.NextTrack => "",
        StreamFunction.PreviousTrack => "",
        StreamFunction.Anc => "",
        StreamFunction.GameMode => "",
        StreamFunction.VolumeUp => "",
        StreamFunction.VolumeDown => "",
        StreamFunction.Disable => "",
        _ => "",
    };

    public static string Glyph(CallFunction f) => f switch
    {
        CallFunction.Answer or CallFunction.EndCall => "",
        CallFunction.AnswerAndSwitch or CallFunction.SwitchCall => "",
        CallFunction.Reject => "",
        CallFunction.VolumeUp => "",
        CallFunction.VolumeDown => "",
        CallFunction.Disable => "",
        _ => "",
    };
}
