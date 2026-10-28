using System.Net.Http;
using System.Text.Json;
using RazerAudioPC.Bluetooth;

namespace RazerAudioPC.Device;

/// <summary>
/// Reads Razer's firmware manifest for this model (same URL as the mobile app,
/// firmware_update_prod_hammerhead_t1v2_hs_url). Read-only: installing stays with the Razer app.
/// </summary>
public static class FirmwareCheck
{
    public const string ManifestUrl = "https://mobileapp-assets.razerzone.com/hammerhead/t1v2/firmware_update.json";

    public sealed record Release(string Version, string Changes);

    public static async Task<Release?> LatestAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            using var doc = JsonDocument.Parse(await http.GetStringAsync(ManifestUrl));
            var root = doc.RootElement;
            return new Release(
                root.GetProperty("firmware_version").GetString() ?? "",
                root.TryGetProperty("changes", out var c) ? c.GetString() ?? "" : "");
        }
        catch (Exception ex)
        {
            BleLog.Write($"[FIRMWARE] manifest check failed: {ex.Message}");
            return null;
        }
    }
}
