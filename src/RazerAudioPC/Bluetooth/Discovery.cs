using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Enumeration;

namespace RazerAudioPC.Bluetooth;

public sealed record BudCandidate(ulong Address, byte? Grs, string Source);

/// <summary>Finds the two buds' BLE addresses (docs/PROTOCOL.md §4.3 and §5).</summary>
public static class Discovery
{
    public const string NameFragment = "Hammerhead";

    public static async Task<List<BudCandidate>> FindAsync(TimeSpan scanTime, CancellationToken ct = default)
    {
        var found = new Dictionary<ulong, BudCandidate>();

        // 1. Live advertisements: Razer manufacturer data [0x08, 0x15, edition, ..., GRS]
        var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
        int advCount = 0;
        var seen = new HashSet<ulong>();
        watcher.Received += (_, args) =>
        {
            Interlocked.Increment(ref advCount);
            lock (seen) seen.Add(args.BluetoothAddress);
            if (args.Advertisement.LocalName?.Contains("Razer", StringComparison.OrdinalIgnoreCase) == true)
            {
                BleLog.Write($"[DISCOVERY] named adv {args.BluetoothAddress:X12} '{args.Advertisement.LocalName}' type={args.AdvertisementType}");
            }
            if (args.Advertisement.ServiceUuids.Contains(Protocol.ServiceUuid))
            {
                BleLog.Write($"[DISCOVERY] FD65 adv {args.BluetoothAddress:X12} name='{args.Advertisement.LocalName}' rssi={args.RawSignalStrengthInDBm}");
                lock (found) found.TryAdd(args.BluetoothAddress, new BudCandidate(args.BluetoothAddress, null, "FD65 advertisement"));
            }
            foreach (var md in args.Advertisement.ManufacturerData)
            {
                if (md.CompanyId != Protocol.RazerCompanyId) continue;
                var data = md.Data.ToArray();
                BleLog.Write($"[DISCOVERY] Razer adv {args.BluetoothAddress:X12} data={Convert.ToHexString(data)}");
                if (data.Length < 3 || data[0] != Protocol.ProductType || data[1] != Protocol.ModelId) continue;
                lock (found) found[args.BluetoothAddress] = new BudCandidate(args.BluetoothAddress, data[^1], "advertisement");
                BleLog.Write($"[DISCOVERY] adv {args.BluetoothAddress:X12} data={Convert.ToHexString(data)}");
            }
        };
        watcher.Start();

        // 2. Windows-paired devices: LE address directly, or classic address with octet 3 + 1
        try
        {
            foreach (var selector in new[]
                     {
                         BluetoothLEDevice.GetDeviceSelectorFromPairingState(true),
                         BluetoothDevice.GetDeviceSelectorFromPairingState(true),
                     })
            {
                var infos = await DeviceInformation.FindAllAsync(selector);
                foreach (var info in infos.Where(i => i.Name.Contains(NameFragment, StringComparison.OrdinalIgnoreCase)))
                {
                    await AddPaired(info, found);
                }
            }
        }
        catch (Exception ex)
        {
            BleLog.Write($"[DISCOVERY] paired lookup failed: {ex.Message}");
        }

        try { await Task.Delay(scanTime, ct); }
        catch (OperationCanceledException) { }
        watcher.Stop();
        BleLog.Write($"[DISCOVERY] scanner: {advCount} adverts from {seen.Count} devices, status={watcher.Status}");

        lock (found)
        {
            // Advertised entries first: they carry the role byte.
            return found.Values.OrderBy(c => c.Grs.HasValue ? 0 : 1).ToList();
        }
    }

    private static async Task AddPaired(DeviceInformation info, Dictionary<ulong, BudCandidate> found)
    {
        try
        {
            using var le = await BluetoothLEDevice.FromIdAsync(info.Id);
            if (le != null)
            {
                lock (found) found.TryAdd(le.BluetoothAddress, new BudCandidate(le.BluetoothAddress, null, "paired LE"));
                return;
            }
        }
        catch { /* not an LE id */ }

        try
        {
            using var bt = await BluetoothDevice.FromIdAsync(info.Id);
            if (bt == null) return;
            ulong le = ClassicToLe(bt.BluetoothAddress);
            BleLog.Write($"[DISCOVERY] classic {bt.BluetoothAddress:X12} -> LE guess {le:X12}");
            lock (found)
            {
                found.TryAdd(le, new BudCandidate(le, null, "derived from classic"));
                // The other bud differs in octet 3 too; Windows may also expose GATT on the classic address itself.
                found.TryAdd(ClassicToLe(le), new BudCandidate(ClassicToLe(le), null, "derived from classic (+2)"));
                found.TryAdd(bt.BluetoothAddress, new BudCandidate(bt.BluetoothAddress, null, "classic address"));
            }
        }
        catch { /* ignore */ }
    }

    /// <summary>AudioTws.getClassicAddress: LE octet[3] − 1 = classic, so LE = classic octet[3] + 1.</summary>
    public static ulong ClassicToLe(ulong classic)
    {
        // Octet index 3 of AA:BB:CC:DD:EE:FF is DD → bits 16..23
        ulong octet = (classic >> 16) & 0xFF;
        octet = (octet + 1) & 0xFF;
        return (classic & ~(0xFFUL << 16)) | (octet << 16);
    }

    public static string Format(ulong address) =>
        string.Join(":", Enumerable.Range(0, 6).Select(i => ((address >> (8 * (5 - i))) & 0xFF).ToString("X2")));
}
