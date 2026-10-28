using System.IO;
using System.Text;
using RazerAudioPC.Bluetooth;

namespace RazerAudioPC;

/// <summary>
/// `RazerAudioPC.exe --probe`: read-only diagnostic. Finds the buds, connects, and runs the
/// same GET queries the official app runs on connect. Writes a log next to the exe.
/// </summary>
public static class Probe
{
    public static async Task<string> RunAsync()
    {
        var log = new StringBuilder();
        void Log(string s) { lock (log) log.AppendLine($"{DateTime.Now:HH:mm:ss.fff} {s}"); }
        BleLog.Sink += Log;

        var candidates = await Discovery.FindAsync(TimeSpan.FromSeconds(12));
        Log($"Candidates: {candidates.Count}");
        foreach (var c in candidates) Log($"  {Discovery.Format(c.Address)} grs={(c.Grs?.ToString("X2") ?? "?")} via {c.Source}");

        foreach (var c in candidates)
        {
            using var ch = new RazerChannel();
            ch.EventReceived += (_, d) => Log($"  [event] {Convert.ToHexString(d)}");
            Log($"Connecting {Discovery.Format(c.Address)}...");
            if (!await ch.ConnectAsync(c.Address))
            {
                Log("  connect failed");
                continue;
            }
            Log("  connected");

            foreach (var (cmd, name) in new (byte, string)[]
                     {
                         (Protocol.Grs, "GRS"), (Protocol.QuickSettings, "Quick settings"), (Protocol.Anc, "ANC"),
                         (Protocol.AutoPause, "Auto pause"), (Protocol.TouchMap, "Touch map"),
                         (Protocol.FirmwareVersion, "Firmware"), (Protocol.Battery, "Battery"),
                         (Protocol.EqPreset, "EQ preset"), (Protocol.EqBands, "EQ bands"),
                         (Protocol.QuickConnectHosts, "QC hosts"), (Protocol.AutoPowerOff, "Auto power off"),
                         (Protocol.DoNotDisturb, "DND"), (Protocol.ChromaEffect, "Chroma effect"),
                         (Protocol.ChromaBrightness, "Chroma brightness"), (Protocol.WearStatus, "Wear"),
                         (Protocol.LowPowerMode, "Low power"),
                     })
            {
                try
                {
                    var resp = await ch.SendAsync(new byte[] { cmd, 0, 0 }, 1000, 2);
                    Log($"  {name,-18} {Convert.ToHexString(resp)}");
                }
                catch (Exception ex)
                {
                    Log($"  {name,-18} ERROR {ex.Message}");
                }
            }
        }

        var path = Path.Combine(AppContext.BaseDirectory, "probe.log");
        await File.WriteAllTextAsync(path, log.ToString());
        return path;
    }
}
