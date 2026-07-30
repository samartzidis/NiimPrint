using Windows.Devices.Bluetooth.Advertisement;

namespace Niimbot;

public sealed record BleDeviceInfo(string Name, ulong Address);

// Port of bluetooth.py's find_device(): scans BLE advertisements and returns
// the first device whose name starts with the given prefix and which does
// not advertise any service UUIDs (mirrors the bleak-based Python check).
public static class DeviceFinder
{
    public static async Task<BleDeviceInfo> FindDeviceAsync(string namePrefix, TimeSpan? scanDuration = null)
    {
        var devices = await ScanAsync(scanDuration ?? TimeSpan.FromSeconds(8));

        var match = devices.Values.FirstOrDefault(d =>
            d.Name.StartsWith(namePrefix, StringComparison.OrdinalIgnoreCase) && !d.HasServiceUuids);

        if (match is null)
            throw new BleException($"Failed to find device {namePrefix}");

        return new BleDeviceInfo(match.Name, match.Address);
    }

    private sealed record ScanResult(string Name, ulong Address, bool HasServiceUuids);

    private static async Task<Dictionary<ulong, ScanResult>> ScanAsync(TimeSpan duration)
    {
        var found = new Dictionary<ulong, ScanResult>();
        var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };

        void OnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
        {
            var name = args.Advertisement.LocalName;
            if (string.IsNullOrEmpty(name))
                return;

            found[args.BluetoothAddress] = new ScanResult(name, args.BluetoothAddress, args.Advertisement.ServiceUuids.Count > 0);
        }

        watcher.Received += OnReceived;
        watcher.Start();
        try
        {
            await Task.Delay(duration);
        }
        finally
        {
            watcher.Stop();
            watcher.Received -= OnReceived;
        }

        return found;
    }
}
