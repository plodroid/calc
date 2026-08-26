using HidSharp;
using System.Buffers.Binary;
using System.Diagnostics;

namespace DualShockStudio.Services;

public sealed class DS4State
{
    public byte LX, LY, RX, RY, L2, R2;
    public short GyroX, GyroY, GyroZ, AccelX, AccelY, AccelZ;
    public int BatteryPercent;
    public bool Charging;
    public bool TouchActive, Touch2Active;
    public int TouchX, TouchY, Touch2X, Touch2Y;
    public ushort SensorTimestamp;
    public long HostTimestampTicks;
    public HashSet<string> Buttons { get; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class DS4Controller : IDisposable
{
    const int SonyVendorId = 0x054C;
    static readonly int[] ProductIds = { 0x05C4, 0x09CC, 0x0BA0 };
    readonly object _ioLock = new();
    readonly object _outputLock = new();
    readonly Dictionary<string, (byte small, byte large)> _rumbleSources = new(StringComparer.OrdinalIgnoreCase);
    HidDevice? _device;
    HidStream? _stream;
    CancellationTokenSource? _cts;
    Task? _readTask;
    byte _r = 0, _g = 80, _b = 255;
    bool _bluetooth;
    long _hzWindowStart = Stopwatch.GetTimestamp();
    int _hzCount;
    double _reportHz;

    public bool IsConnected => _stream != null;
    public bool IsBluetooth => _bluetooth;
    public string DeviceName { get; private set; } = "No controller";
    public DS4State LastState { get; private set; } = new();
    public double ReportHz => Volatile.Read(ref _reportHz);

    public event Action<bool>? ConnectionChanged;
    public event Action<DS4State>? StateChanged;
    public event Action<string>? Log;

    public bool Connect()
    {
        Disconnect();
        try
        {
            var devices = DeviceList.Local.GetHidDevices(SonyVendorId)
                .Where(d => ProductIds.Contains(d.ProductID))
                .OrderByDescending(d => d.GetMaxInputReportLength())
                .ToList();

            foreach (var dev in devices)
            {
                try
                {
                    if (!dev.TryOpen(out HidStream? stream) || stream == null) continue;
                    stream.ReadTimeout = 1000;
                    stream.WriteTimeout = 1000;
                    _device = dev;
                    _stream = stream;
                    _bluetooth = dev.GetMaxInputReportLength() >= 78;
                    try { DeviceName = dev.GetProductName(); } catch { DeviceName = $"DualShock 4 ({dev.ProductID:X4})"; }
                    _hzWindowStart = Stopwatch.GetTimestamp(); _hzCount = 0; _reportHz = 0;
                    _cts = new CancellationTokenSource();
                    _readTask = Task.Run(() => ReadLoop(_cts.Token));
                    Log?.Invoke($"Connected: {DeviceName} • {(_bluetooth ? "Bluetooth" : "USB")}");
                    ConnectionChanged?.Invoke(true);
                    SendOutput();
                    return true;
                }
                catch (Exception ex) { Log?.Invoke($"Open failed: {ex.Message}"); }
            }
            Log?.Invoke("No compatible DualShock 4 found. Connect by USB/Bluetooth and press Reconnect.");
        }
        catch (Exception ex) { Log?.Invoke("Controller scan failed: " + ex.Message); }
        ConnectionChanged?.Invoke(false);
        return false;
    }

    public void Disconnect()
    {
        try { _cts?.Cancel(); } catch { }
        lock (_ioLock)
        {
            try { _stream?.Dispose(); } catch { }
            _stream = null; _device = null;
        }
        _cts?.Dispose(); _cts = null;
        lock (_outputLock) _rumbleSources.Clear();
        if (DeviceName != "No controller") ConnectionChanged?.Invoke(false);
        DeviceName = "No controller";
    }

    void ReadLoop(CancellationToken token)
    {
        byte[]? buffer = null;
        while (!token.IsCancellationRequested && _stream != null && _device != null)
        {
            try
            {
                buffer ??= new byte[Math.Max(78, _device.GetMaxInputReportLength())];
                int read;
                lock (_ioLock)
                {
                    if (_stream == null) break;
                    read = _stream.Read(buffer, 0, buffer.Length);
                }
                if (read >= 10)
                {
                    var state = Parse(buffer.AsSpan(0, read));
                    if (state != null)
                    {
                        state.HostTimestampTicks = Stopwatch.GetTimestamp();
                        LastState = state;
                        UpdateReportRate();
                        StateChanged?.Invoke(state);
                    }
                }
            }
            catch (TimeoutException) { }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested) Log?.Invoke("Controller read stopped: " + ex.Message);
                break;
            }
        }
        if (!token.IsCancellationRequested)
        {
            lock (_ioLock) { try { _stream?.Dispose(); } catch { } _stream = null; }
            ConnectionChanged?.Invoke(false);
        }
    }

    void UpdateReportRate()
    {
        _hzCount++;
        long now = Stopwatch.GetTimestamp();
        double seconds = (now - _hzWindowStart) / (double)Stopwatch.Frequency;
        if (seconds >= 1.0)
        {
            Volatile.Write(ref _reportHz, _hzCount / seconds);
            _hzCount = 0; _hzWindowStart = now;
        }
    }

    static DS4State? Parse(ReadOnlySpan<byte> data)
    {
        int common;
        if (data.Length >= 64 && data[0] == 0x01) common = 1;
        else if (data.Length >= 35 && data[0] == 0x11) common = 3;
        else return null;
        if (data.Length < common + 32) return null;

        var s = new DS4State
        {
            LX = data[common], LY = data[common + 1], RX = data[common + 2], RY = data[common + 3],
            L2 = data[common + 7], R2 = data[common + 8],
            SensorTimestamp = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(common + 9, 2)),
            GyroX = BinaryPrimitives.ReadInt16LittleEndian(data.Slice(common + 12, 2)),
            GyroY = BinaryPrimitives.ReadInt16LittleEndian(data.Slice(common + 14, 2)),
            GyroZ = BinaryPrimitives.ReadInt16LittleEndian(data.Slice(common + 16, 2)),
            AccelX = BinaryPrimitives.ReadInt16LittleEndian(data.Slice(common + 18, 2)),
            AccelY = BinaryPrimitives.ReadInt16LittleEndian(data.Slice(common + 20, 2)),
            AccelZ = BinaryPrimitives.ReadInt16LittleEndian(data.Slice(common + 22, 2))
        };

        byte b0 = data[common + 4], b1 = data[common + 5], b2 = data[common + 6];
        AddIf(s, b0, 0x10, "Square"); AddIf(s, b0, 0x20, "Cross"); AddIf(s, b0, 0x40, "Circle"); AddIf(s, b0, 0x80, "Triangle");
        AddIf(s, b1, 0x01, "L1"); AddIf(s, b1, 0x02, "R1");
        AddIf(s, b1, 0x10, "Share"); AddIf(s, b1, 0x20, "Options"); AddIf(s, b1, 0x40, "L3"); AddIf(s, b1, 0x80, "R3");
        AddIf(s, b2, 0x01, "PS"); AddIf(s, b2, 0x02, "TouchpadClick");

        switch (b0 & 0x0F)
        {
            case 0: s.Buttons.Add("DPadUp"); break;
            case 1: s.Buttons.UnionWith(new[] { "DPadUp", "DPadRight" }); break;
            case 2: s.Buttons.Add("DPadRight"); break;
            case 3: s.Buttons.UnionWith(new[] { "DPadRight", "DPadDown" }); break;
            case 4: s.Buttons.Add("DPadDown"); break;
            case 5: s.Buttons.UnionWith(new[] { "DPadDown", "DPadLeft" }); break;
            case 6: s.Buttons.Add("DPadLeft"); break;
            case 7: s.Buttons.UnionWith(new[] { "DPadLeft", "DPadUp" }); break;
        }

        byte status0 = data[common + 29];
        int battery = status0 & 0x0F;
        s.BatteryPercent = battery >= 11 ? 100 : Math.Clamp(battery * 10, 0, 100);
        s.Charging = (status0 & 0x10) != 0;

        int touchCountOffset = common + 32;
        if (data.Length > touchCountOffset && data[touchCountOffset] > 0)
        {
            int p1 = touchCountOffset + 2;
            if (data.Length >= p1 + 8)
            {
                ParseTouch(data.Slice(p1, 4), out s.TouchActive, out s.TouchX, out s.TouchY);
                ParseTouch(data.Slice(p1 + 4, 4), out s.Touch2Active, out s.Touch2X, out s.Touch2Y);
            }
        }
        return s;
    }

    static void ParseTouch(ReadOnlySpan<byte> p, out bool active, out int x, out int y)
    {
        active = (p[0] & 0x80) == 0;
        x = p[1] | ((p[2] & 0x0F) << 8);
        y = ((p[2] & 0xF0) >> 4) | (p[3] << 4);
        if (!active) { x = 0; y = 0; }
    }

    static void AddIf(DS4State s, byte value, byte mask, string name) { if ((value & mask) != 0) s.Buttons.Add(name); }

    public void SetRumble(byte small, byte large) => SetRumbleSource("manual", small, large);
    public void SetRumbleSource(string source, byte small, byte large)
    {
        lock (_outputLock)
        {
            if (small == 0 && large == 0) _rumbleSources.Remove(source);
            else _rumbleSources[source] = (small, large);
        }
        SendOutput();
    }
    public void ClearRumbleSource(string source)
    {
        lock (_outputLock) _rumbleSources.Remove(source);
        SendOutput();
    }
    public void StopAllRumble()
    {
        lock (_outputLock) _rumbleSources.Clear();
        SendOutput();
    }

    public void SetLightbar(byte r, byte g, byte b) { lock (_outputLock) { _r = r; _g = g; _b = b; } SendOutput(); }

    public async Task PulseAsync(int strength = 160, int milliseconds = 130, string source = "pulse")
    {
        byte v = (byte)Math.Clamp(strength, 0, 255);
        string id = source + "-" + Guid.NewGuid().ToString("N");
        SetRumbleSource(id, (byte)Math.Clamp((int)(v * 0.55), 0, 255), v);
        try { await Task.Delay(Math.Max(20, milliseconds)); } catch { }
        ClearRumbleSource(id);
    }

    void SendOutput()
    {
        lock (_ioLock)
        {
            if (_stream == null) return;
            try
            {
                byte small = 0, large = 0, r, g, b;
                lock (_outputLock)
                {
                    foreach (var v in _rumbleSources.Values) { if (v.small > small) small = v.small; if (v.large > large) large = v.large; }
                    r = _r; g = _g; b = _b;
                }
                if (!_bluetooth)
                {
                    var report = new byte[32];
                    report[0] = 0x05; report[1] = 0xFF; report[2] = 0x04; report[3] = 0x00;
                    report[4] = small; report[5] = large;
                    report[6] = r; report[7] = g; report[8] = b;
                    _stream.Write(report);
                }
                else
                {
                    var report = new byte[78];
                    report[0] = 0x11; report[1] = 0xC0; report[2] = 0x20;
                    report[3] = 0xF3; report[4] = 0x04; report[5] = 0x00;
                    report[6] = small; report[7] = large;
                    report[8] = r; report[9] = g; report[10] = b;
                    uint crc = Crc32Ds4(report.AsSpan(0, 74));
                    BinaryPrimitives.WriteUInt32LittleEndian(report.AsSpan(74, 4), crc);
                    _stream.Write(report);
                }
            }
            catch (Exception ex) { Log?.Invoke("Output write failed: " + ex.Message); }
        }
    }

    static uint Crc32Ds4(ReadOnlySpan<byte> report)
    {
        uint crc = 0xFFFFFFFF;
        crc = CrcByte(crc, 0xA2);
        foreach (byte b in report) crc = CrcByte(crc, b);
        return ~crc;
    }
    static uint CrcByte(uint crc, byte b)
    {
        crc ^= b;
        for (int i = 0; i < 8; i++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        return crc;
    }

    public void Dispose() => Disconnect();
}
