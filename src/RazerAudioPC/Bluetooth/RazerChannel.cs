using System.Diagnostics;

namespace RazerAudioPC.Bluetooth;

/// <summary>
/// Request/response layer over one bud's GATT link (docs/PROTOCOL.md §1.4–1.5):
/// raw command bytes are written as-is, and the next notification whose byte0 equals the
/// request's byte0 is the response. One request in flight at a time; retries on timeout.
/// Notifications with byte1 == 0x02 are unsolicited events.
/// </summary>
public sealed class RazerChannel : IDisposable
{
    // byte1: 0x00/0x01 = reply (the firmware uses 0x01; the app never checks it), 0x02 = unsolicited event
    public const byte StatusEvent = 0x02;

    private readonly BleConnection _ble = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private TaskCompletionSource<byte[]>? _pending;
    private byte _pendingCmd;

    /// <summary>Unsolicited device events (battery, ANC toggled by gesture, wear...).</summary>
    public event Action<RazerChannel, byte[]>? EventReceived;
    public event Action<RazerChannel>? Disconnected;

    public ulong Address { get; private set; }
    public bool IsConnected => _ble.IsConnected;

    /// <summary>GRS role byte (§3.1).</summary>
    public byte Grs { get; set; }
    public bool IsLeft => (Grs & 0x01) == 0;
    public bool IsPrimary => (Grs & 0x04) == 0;
    public bool IsStandalone => (Grs & 0x02) == 0;

    public RazerChannel()
    {
        _ble.NotificationReceived += OnNotification;
        _ble.Disconnected += () => Disconnected?.Invoke(this);
    }

    public async Task<bool> ConnectAsync(ulong address)
    {
        Address = address;
        return await _ble.ConnectAsync(address, Protocol.ServiceUuid, Protocol.WriteUuid, Protocol.NotifyUuid);
    }

    private void OnNotification(byte[] data)
    {
        if (data.Length == 0) return;
        var pending = _pending;
        bool isEvent = data.Length > 1 && data[1] == StatusEvent;
        if (pending != null && data[0] == _pendingCmd && !isEvent)
        {
            _pending = null;
            pending.TrySetResult(data);
            return;
        }
        EventReceived?.Invoke(this, data);
    }

    /// <summary>Sends a command and waits for its response. Throws <see cref="TimeoutException"/> after all tries.</summary>
    public async Task<byte[]> SendAsync(byte[] command, int timeoutMs = 500, int tries = 2, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            for (int attempt = 1; attempt <= tries; attempt++)
            {
                var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
                _pendingCmd = command[0];
                _pending = tcs;

                if (!await _ble.WriteAsync(command))
                {
                    _pending = null;
                    BleLog.Write($"[RAZER] write failed {Convert.ToHexString(command)} (try {attempt})");
                    await Task.Delay(100, ct);
                    continue;
                }

                var done = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs, ct));
                if (done == tcs.Task) return tcs.Task.Result;
                _pending = null;
                BleLog.Write($"[RAZER] timeout {Convert.ToHexString(command)} (try {attempt})");
            }
            throw new TimeoutException($"No response to {Convert.ToHexString(command)}");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>GET helper: returns the payload (bytes from index 3).</summary>
    public async Task<byte[]> GetAsync(byte cmd, int timeoutMs = 500, int tries = 2)
    {
        var resp = await SendAsync(new byte[] { cmd, 0, 0 }, timeoutMs, tries);
        return resp.Length > 3 ? resp[3..] : Array.Empty<byte>();
    }

    /// <summary>SET helper: [cmd|0x80, 0, len, payload...]; returns the response payload.</summary>
    public async Task<byte[]> SetAsync(byte cmd, byte[] payload, int timeoutMs = 500, int tries = 2)
    {
        var packet = new byte[3 + payload.Length];
        packet[0] = cmd;
        packet[2] = (byte)payload.Length;
        payload.CopyTo(packet, 3);
        var resp = await SendAsync(packet, timeoutMs, tries);
        return resp.Length > 3 ? resp[3..] : Array.Empty<byte>();
    }

    public void Dispose() => _ble.Dispose();
}
