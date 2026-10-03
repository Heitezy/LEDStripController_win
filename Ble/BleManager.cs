using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace LEDStripController.Ble;

public sealed record DiscoveredDevice(ulong Address, string Name, int Rssi)
{
    public string Detail => $"{AddressText}  ·  {Rssi} dBm";
    public bool IsLikelyLedStrip =>
        Name.Contains("BLEDOM", StringComparison.OrdinalIgnoreCase) || Name.Contains("BJ_LED", StringComparison.OrdinalIgnoreCase);
    public string AddressText =>
        string.Join(":", BitConverter.GetBytes(Address).Take(6).Reverse().Select(b => b.ToString("X2")));
}

public sealed class BleManager
{
    static Guid Short(ushort id) => new($"0000{id:X4}-0000-1000-8000-00805F9B34FB");

    // ELK-BLEDOM primary (FFF0/FFF3), alternate (FFE5/FFE9); BJ_LED uses characteristic EE01.
    static readonly Guid ElkSvcA = Short(0xFFF0), ElkChrA = Short(0xFFF3);
    static readonly Guid ElkSvcB = Short(0xFFE5), ElkChrB = Short(0xFFE9);
    static readonly Guid BjChr = Short(0xEE01);

    BluetoothLEAdvertisementWatcher? _watcher;
    readonly Dictionary<ulong, (string Name, int Rssi, long Tick)> _seen = new();

    BluetoothLEDevice? _device;
    GattSession? _session;
    List<GattDeviceService> _services = new();
    GattCharacteristic? _char;
    GattWriteOption _writeOption = GattWriteOption.WriteWithoutResponse;
    readonly SemaphoreSlim _writeLock = new(1, 1);

    public event Action<DiscoveredDevice>? DeviceFound;
    public event Action<bool>? ConnectionChanged;
    public event Action<string>? ScanError;

    public StripType Type { get; private set; } = StripType.ElkBledom;
    public string DeviceName => _device?.Name ?? "";
    public bool IsScanning => _watcher?.Status == BluetoothLEAdvertisementWatcherStatus.Started;
    public bool IsConnected =>
        _device is { ConnectionStatus: BluetoothConnectionStatus.Connected } && _char != null;

    // ---------- scanning ----------
    public void StartScan()
    {
        StopScan();
        lock (_seen) _seen.Clear();
        _watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
        _watcher.Received += OnAdvertisement;
        _watcher.Stopped += (w, e) =>
        {
            if (e.Error != BluetoothError.Success)
                ScanError?.Invoke($"Bluetooth scan stopped: {e.Error}. Is Bluetooth turned on?");
        };
        _watcher.Start();
    }

    public void StopScan()
    {
        if (_watcher == null) return;
        try { _watcher.Stop(); } catch { }
        _watcher = null;
    }

    void OnAdvertisement(BluetoothLEAdvertisementWatcher w, BluetoothLEAdvertisementReceivedEventArgs a)
    {
        var name = a.Advertisement.LocalName;
        if (string.IsNullOrWhiteSpace(name)) return;   // Android ignores unnamed devices too
        int rssi = a.RawSignalStrengthInDBm;
        long now = Environment.TickCount64;
        lock (_seen)
        {
            // Throttle RSSI-only refreshes to 1/s per device so the list doesn't thrash
            if (_seen.TryGetValue(a.BluetoothAddress, out var old) && old.Name == name &&
                (old.Rssi == rssi || now - old.Tick < 1000)) return;
            _seen[a.BluetoothAddress] = (name, rssi, now);
        }
        DeviceFound?.Invoke(new DiscoveredDevice(a.BluetoothAddress, name, rssi));
    }

    // ---------- connection ----------
    /// <summary>Connects straight from a known address (no scan needed) — used for auto-reconnect.</summary>
    public async Task<bool> ConnectAsync(ulong address)
    {
        Disconnect(raise: false);
        try
        {
            var dev = await BluetoothLEDevice.FromBluetoothAddressAsync(address);
            if (dev == null) return false;

            var svcResult = await dev.GetGattServicesAsync(BluetoothCacheMode.Uncached);
            if (svcResult.Status != GattCommunicationStatus.Success)
            {
                dev.Dispose();
                return false;
            }

            var found = new List<(Guid Svc, Guid Chr, GattCharacteristic C)>();
            foreach (var svc in svcResult.Services)
            {
                var cr = await svc.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
                if (cr.Status != GattCommunicationStatus.Success) continue;
                foreach (var c in cr.Characteristics) found.Add((svc.Uuid, c.Uuid, c));
            }

            static bool Writable(GattCharacteristic c) =>
                (c.CharacteristicProperties &
                 (GattCharacteristicProperties.Write | GattCharacteristicProperties.WriteWithoutResponse)) != 0;

            // 1) BJ_LED characteristic (any service)
            // 2) ELK-BLEDOM primary service, else alternate: primary char, alt char, else first writable
            // 3) Fallback: first writable characteristic anywhere (BJ_LED if the name says so)
            StripType type = StripType.ElkBledom;
            GattCharacteristic? ch = found.FirstOrDefault(f => f.Chr == BjChr).C;
            if (ch != null)
            {
                type = StripType.BjLed;
            }
            else
            {
                Guid? svcId = found.Any(f => f.Svc == ElkSvcA) ? ElkSvcA
                            : found.Any(f => f.Svc == ElkSvcB) ? ElkSvcB : null;
                if (svcId != null)
                {
                    var inSvc = found.Where(f => f.Svc == svcId).ToList();
                    ch = inSvc.FirstOrDefault(f => f.Chr == ElkChrA).C
                      ?? inSvc.FirstOrDefault(f => f.Chr == ElkChrB).C
                      ?? inSvc.FirstOrDefault(f => Writable(f.C)).C;
                }
                else
                {
                    if (dev.Name?.Contains("BJ_LED", StringComparison.OrdinalIgnoreCase) == true)
                        type = StripType.BjLed;
                    ch = found.FirstOrDefault(f => Writable(f.C)).C;
                }
            }
            if (ch == null) { dev.Dispose(); return false; }

            _writeOption = (ch.CharacteristicProperties & GattCharacteristicProperties.WriteWithoutResponse) != 0
                ? GattWriteOption.WriteWithoutResponse
                : GattWriteOption.WriteWithResponse;

            try
            {
                _session = await GattSession.FromDeviceIdAsync(dev.BluetoothDeviceId);
                _session.MaintainConnection = true;
            }
            catch { }

            _services = svcResult.Services.ToList();
            _device = dev;
            _char = ch;
            Type = type;
            dev.ConnectionStatusChanged += OnConnectionStatusChanged;
            ConnectionChanged?.Invoke(true);
            return true;
        }
        catch
        {
            Disconnect(raise: false);
            return false;
        }
    }

    void OnConnectionStatusChanged(BluetoothLEDevice sender, object args) =>
        ConnectionChanged?.Invoke(sender.ConnectionStatus == BluetoothConnectionStatus.Connected);

    public void Disconnect() => Disconnect(raise: true);

    void Disconnect(bool raise)
    {
        bool had = _device != null;
        _char = null;
        if (_device != null) _device.ConnectionStatusChanged -= OnConnectionStatusChanged;
        foreach (var s in _services) { try { s.Dispose(); } catch { } }
        _services = new();
        try { _session?.Dispose(); } catch { }
        _session = null;
        try { _device?.Dispose(); } catch { }
        _device = null;
        if (raise && had) ConnectionChanged?.Invoke(false);
    }

    // ---------- writing ----------
    /// <param name="dropIfBusy">For high-rate frames: skip instead of queueing so latency stays low.</param>
    public async Task<bool> WriteAsync(byte[] data, bool dropIfBusy)
    {
        var ch = _char;
        if (ch == null) return false;

        if (dropIfBusy) { if (!await _writeLock.WaitAsync(0)) return false; }
        else await _writeLock.WaitAsync();

        try
        {
            using var writer = new DataWriter();
            writer.WriteBytes(data);
            var status = await ch.WriteValueAsync(writer.DetachBuffer(), _writeOption);
            return status == GattCommunicationStatus.Success;
        }
        catch { return false; }
        finally { _writeLock.Release(); }
    }
}
