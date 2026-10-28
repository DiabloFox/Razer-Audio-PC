using System.Diagnostics;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace RazerAudioPC.Bluetooth;

/// <summary>
/// A GATT connection to one earbud: locates the device, opens the Razer control
/// characteristics, writes raw packets and surfaces notifications.
/// </summary>
public sealed class BleConnection : IDisposable
{
    private BluetoothLEDevice? _device;
    private GattCharacteristic? _write;
    private GattCharacteristic? _notify;

    public event Action<byte[]>? NotificationReceived;
    public event Action? Disconnected;

    public ulong Address => _device?.BluetoothAddress ?? 0;
    public string Name => _device?.Name ?? "";
    public bool IsConnected => _device?.ConnectionStatus == BluetoothConnectionStatus.Connected && _write != null;

    /// <summary>
    /// Finds candidate earbud BLE addresses: devices Windows already knows (paired LE or
    /// classic with the same address) whose name matches, then live advertisements that
    /// carry the Razer service.
    /// </summary>
    public static async Task<List<ulong>> FindCandidatesAsync(string nameFragment, Guid scanService, TimeSpan scanTime, CancellationToken ct)
    {
        var found = new List<ulong>();

        foreach (var selector in new[]
                 {
                     BluetoothLEDevice.GetDeviceSelectorFromPairingState(true),
                     BluetoothDevice.GetDeviceSelectorFromPairingState(true),
                 })
        {
            try
            {
                var infos = await DeviceInformation.FindAllAsync(selector);
                foreach (var info in infos.Where(i => i.Name.Contains(nameFragment, StringComparison.OrdinalIgnoreCase)))
                {
                    ulong addr = await AddressOf(info);
                    if (addr != 0 && !found.Contains(addr)) found.Add(addr);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BLE] enumerate failed: {ex.Message}");
            }
        }

        // Live scan: the buds advertise the Razer service while connected to a phone/PC.
        var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
        var tcs = new TaskCompletionSource();
        watcher.Received += (_, args) =>
        {
            bool match = args.Advertisement.ServiceUuids.Contains(scanService)
                         || (args.Advertisement.LocalName?.Contains(nameFragment, StringComparison.OrdinalIgnoreCase) ?? false);
            if (!match) return;
            lock (found)
            {
                if (!found.Contains(args.BluetoothAddress)) found.Add(args.BluetoothAddress);
            }
        };
        watcher.Start();
        try { await Task.Delay(scanTime, ct); }
        catch (OperationCanceledException) { }
        watcher.Stop();

        lock (found) return found.ToList();
    }

    private static async Task<ulong> AddressOf(DeviceInformation info)
    {
        try
        {
            using var le = await BluetoothLEDevice.FromIdAsync(info.Id);
            if (le != null) return le.BluetoothAddress;
        }
        catch { /* not an LE id */ }
        try
        {
            using var bt = await BluetoothDevice.FromIdAsync(info.Id);
            if (bt != null) return bt.BluetoothAddress;
        }
        catch { /* ignore */ }
        return 0;
    }

    /// <summary>Opens the GATT service and characteristics on the device at <paramref name="address"/>.</summary>
    public async Task<bool> ConnectAsync(ulong address, Guid service, Guid writeChar, Guid notifyChar)
    {
        Dispose();
        try
        {
            _device = await BluetoothLEDevice.FromBluetoothAddressAsync(address);
            if (_device == null) return false;

            var services = await _device.GetGattServicesForUuidAsync(service, BluetoothCacheMode.Uncached);
            if (services.Status != GattCommunicationStatus.Success || services.Services.Count == 0)
            {
                Debug.WriteLine($"[BLE] {address:X12}: service {service} not found ({services.Status})");
                Dispose();
                return false;
            }

            var svc = services.Services[0];
            var access = await svc.RequestAccessAsync();
            Debug.WriteLine($"[BLE] access: {access}");

            var w = await svc.GetCharacteristicsForUuidAsync(writeChar, BluetoothCacheMode.Uncached);
            var r = await svc.GetCharacteristicsForUuidAsync(notifyChar, BluetoothCacheMode.Uncached);
            if (w.Status != GattCommunicationStatus.Success || w.Characteristics.Count == 0 ||
                r.Status != GattCommunicationStatus.Success || r.Characteristics.Count == 0)
            {
                Debug.WriteLine("[BLE] characteristics not found");
                Dispose();
                return false;
            }

            _write = w.Characteristics[0];
            _notify = r.Characteristics[0];

            _notify.ValueChanged += OnValueChanged;
            var cccd = _notify.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Indicate)
                       && !_notify.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify)
                ? GattClientCharacteristicConfigurationDescriptorValue.Indicate
                : GattClientCharacteristicConfigurationDescriptorValue.Notify;
            var status = await _notify.WriteClientCharacteristicConfigurationDescriptorAsync(cccd);
            if (status != GattCommunicationStatus.Success)
            {
                Debug.WriteLine($"[BLE] CCCD write failed: {status}");
                Dispose();
                return false;
            }

            _device.ConnectionStatusChanged += OnConnectionStatusChanged;
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[BLE] connect failed: {ex}");
            Dispose();
            return false;
        }
    }

    public async Task<bool> WriteAsync(byte[] data)
    {
        var ch = _write ?? throw new InvalidOperationException("Not connected");
        var option = ch.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Write)
            ? GattWriteOption.WriteWithResponse
            : GattWriteOption.WriteWithoutResponse;
        var writer = new DataWriter();
        writer.WriteBytes(data);
        var result = await ch.WriteValueWithResultAsync(writer.DetachBuffer(), option);
        Debug.WriteLine($"[BLE] >> {Convert.ToHexString(data)} ({result.Status})");
        return result.Status == GattCommunicationStatus.Success;
    }

    private void OnValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        var reader = DataReader.FromBuffer(args.CharacteristicValue);
        var bytes = new byte[reader.UnconsumedBufferLength];
        reader.ReadBytes(bytes);
        Debug.WriteLine($"[BLE] << {Convert.ToHexString(bytes)}");
        NotificationReceived?.Invoke(bytes);
    }

    private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        if (sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected) Disconnected?.Invoke();
    }

    public void Dispose()
    {
        if (_notify != null) _notify.ValueChanged -= OnValueChanged;
        if (_device != null) _device.ConnectionStatusChanged -= OnConnectionStatusChanged;
        _notify = null;
        _write = null;
        _device?.Dispose();
        _device = null;
    }
}
