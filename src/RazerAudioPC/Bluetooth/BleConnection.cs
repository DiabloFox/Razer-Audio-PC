using System.Diagnostics;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
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

    /// <summary>Opens the GATT service and characteristics on the device at <paramref name="address"/>.</summary>
    public async Task<bool> ConnectAsync(ulong address, Guid service, Guid writeChar, Guid notifyChar)
    {
        Dispose();
        try
        {
            _device = await BluetoothLEDevice.FromBluetoothAddressAsync(address);
            if (_device == null)
            {
                BleLog.Write($"[BLE] {address:X12}: no LE device at this address");
                return false;
            }
            BleLog.Write($"[BLE] {address:X12}: '{_device.Name}' status={_device.ConnectionStatus}");

            var services = await _device.GetGattServicesForUuidAsync(service, BluetoothCacheMode.Uncached);
            if (services.Status is GattCommunicationStatus.AccessDenied or GattCommunicationStatus.ProtocolError
                && !_device.DeviceInformation.Pairing.IsPaired && _device.DeviceInformation.Pairing.CanPair)
            {
                // The official app bonds the LE link (requirePair); do the same once.
                var custom = _device.DeviceInformation.Pairing.Custom;
                custom.PairingRequested += (_, a) => a.Accept();
                var pair = await custom.PairAsync(Windows.Devices.Enumeration.DevicePairingKinds.ConfirmOnly
                                                  | Windows.Devices.Enumeration.DevicePairingKinds.ConfirmPinMatch,
                                                  Windows.Devices.Enumeration.DevicePairingProtectionLevel.Encryption);
                BleLog.Write($"[BLE] {address:X12}: pairing {pair.Status}");
                services = await _device.GetGattServicesForUuidAsync(service, BluetoothCacheMode.Uncached);
            }
            if (services.Status != GattCommunicationStatus.Success || services.Services.Count == 0)
            {
                BleLog.Write($"[BLE] {address:X12}: service {service} not found ({services.Status})");
                var all = await _device.GetGattServicesAsync(BluetoothCacheMode.Uncached);
                BleLog.Write($"[BLE] all services ({all.Status}): {string.Join(", ", all.Services.Select(s => s.Uuid))}");
                Dispose();
                return false;
            }

            var svc = services.Services[0];
            var access = await svc.RequestAccessAsync();
            BleLog.Write($"[BLE] access: {access}");

            var w = await svc.GetCharacteristicsForUuidAsync(writeChar, BluetoothCacheMode.Uncached);
            var r = await svc.GetCharacteristicsForUuidAsync(notifyChar, BluetoothCacheMode.Uncached);
            if (w.Status != GattCommunicationStatus.Success || w.Characteristics.Count == 0 ||
                r.Status != GattCommunicationStatus.Success || r.Characteristics.Count == 0)
            {
                BleLog.Write("[BLE] characteristics not found");
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
                BleLog.Write($"[BLE] CCCD write failed: {status}");
                Dispose();
                return false;
            }

            _device.ConnectionStatusChanged += OnConnectionStatusChanged;
            return true;
        }
        catch (Exception ex)
        {
            BleLog.Write($"[BLE] connect failed: {ex}");
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
        BleLog.Write($"[BLE] >> {Convert.ToHexString(data)} ({result.Status})");
        return result.Status == GattCommunicationStatus.Success;
    }

    private void OnValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        var reader = DataReader.FromBuffer(args.CharacteristicValue);
        var bytes = new byte[reader.UnconsumedBufferLength];
        reader.ReadBytes(bytes);
        BleLog.Write($"[BLE] << {Convert.ToHexString(bytes)}");
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
