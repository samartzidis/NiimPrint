using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Foundation;
using Windows.Storage.Streams;

namespace Niimbot;

// Port of bluetooth.py's BLETransport, using the native WinRT Bluetooth LE
// APIs directly (the same stack bleak itself calls into on Windows) instead
// of a cross-platform BLE wrapper.
public sealed class BleTransport
{
    private BluetoothLEDevice? _device;
    private GattCharacteristic? _characteristic;
    private TypedEventHandler<GattCharacteristic, GattValueChangedEventArgs>? _notifyHandler;

    public string DeviceName => _device?.Name ?? string.Empty;

    public bool IsConnected =>
        _device is not null && _device.ConnectionStatus == BluetoothConnectionStatus.Connected;

    public async Task<bool> ConnectAsync(ulong address)
    {
        _device = await BluetoothLEDevice.FromBluetoothAddressAsync(address);
        if (_device is null)
            throw new BleException($"Failed to connect to the BLE device at {address:X}");

        await FindCharacteristicAsync();
        return true;
    }

    // Port of printer.py's find_characteristics(): the printer exposes exactly
    // one service with exactly one characteristic that supports read,
    // write-without-response and notify - that's the one we talk to.
    private async Task FindCharacteristicAsync()
    {
        var servicesResult = await _device!.GetGattServicesAsync(BluetoothCacheMode.Uncached);
        if (servicesResult.Status != GattCommunicationStatus.Success)
            throw new BleException($"Failed to enumerate services: {servicesResult.Status}");

        foreach (var service in servicesResult.Services)
        {
            var charsResult = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
            if (charsResult.Status != GattCommunicationStatus.Success || charsResult.Characteristics.Count != 1)
                continue;

            var characteristic = charsResult.Characteristics[0];
            const GattCharacteristicProperties needed =
                GattCharacteristicProperties.Read
                | GattCharacteristicProperties.WriteWithoutResponse
                | GattCharacteristicProperties.Notify;

            if ((characteristic.CharacteristicProperties & needed) == needed)
            {
                _characteristic = characteristic;
                return;
            }
        }

        throw new PrinterException("Cannot find bluetooth characteristics.");
    }

    public void Disconnect()
    {
        if (_characteristic is not null && _notifyHandler is not null)
            _characteristic.ValueChanged -= _notifyHandler;

        _notifyHandler = null;
        _characteristic = null;
        _device?.Dispose();
        _device = null;
    }

    public async Task WriteAsync(byte[] data)
    {
        if (_characteristic is null || !IsConnected)
            throw new BleException("BLE client is not connected.");

        using var writer = new DataWriter();
        writer.WriteBytes(data);
        var status = await _characteristic.WriteValueAsync(writer.DetachBuffer(), GattWriteOption.WriteWithoutResponse);
        if (status != GattCommunicationStatus.Success)
            throw new BleException($"Write failed: {status}");
    }

    public async Task StartNotificationAsync(TypedEventHandler<GattCharacteristic, GattValueChangedEventArgs> handler)
    {
        if (_characteristic is null || !IsConnected)
            throw new BleException("BLE client is not connected.");

        _notifyHandler = handler;
        _characteristic.ValueChanged += handler;
        var status = await _characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
            GattClientCharacteristicConfigurationDescriptorValue.Notify);
        if (status != GattCommunicationStatus.Success)
            throw new BleException($"Failed to enable notifications: {status}");
    }

    public async Task StopNotificationAsync()
    {
        if (_characteristic is null)
            return;

        if (_notifyHandler is not null)
        {
            _characteristic.ValueChanged -= _notifyHandler;
            _notifyHandler = null;
        }

        if (IsConnected)
        {
            await _characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
                GattClientCharacteristicConfigurationDescriptorValue.None);
        }
    }

    public static byte[] ToByteArray(IBuffer buffer) => buffer.ToArray();
}
