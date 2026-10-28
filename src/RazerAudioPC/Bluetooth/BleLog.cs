using System.Diagnostics;

namespace RazerAudioPC.Bluetooth;

/// <summary>Debug output for the BLE stack; the probe and the in-app log attach a sink.</summary>
public static class BleLog
{
    public static event Action<string>? Sink;

    public static void Write(string message)
    {
        Debug.WriteLine(message);
        Sink?.Invoke(message);
    }
}
