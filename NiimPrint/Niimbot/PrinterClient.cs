using Serilog;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace Niimbot;

public enum InfoKind
{
    Density = 1,
    PrintSpeed = 2,
    LabelType = 3,
    LanguageType = 6,
    AutoShutdownTime = 7,
    DeviceType = 8,
    SoftVersion = 9,
    Battery = 10,
    DeviceSerial = 11,
    HardVersion = 12,
}

internal enum RequestCode : byte
{
    GetInfo = 0x40,
    GetRfid = 0x1A,
    Heartbeat = 0xDC,
    SetLabelType = 0x23,
    SetLabelDensity = 0x21,
    StartPrint = 0x01,
    EndPrint = 0xF3,
    StartPagePrint = 0x03,
    EndPagePrint = 0xE3,
    AllowPrintClear = 0x20,
    SetDimension = 0x13,
    SetQuantity = 0x15,
    GetPrintStatus = 0xA3,
}

public sealed record RfidInfo(string Uuid, string Barcode, string Serial, int UsedLen, int TotalLen, int Type);

public sealed record HeartbeatInfo(int? ClosingState, int? PowerLevel, int? PaperState, int? RfidReadState);

public sealed record PrintStatus(int Page, int Progress1, int Progress2);

// Port of printer.py's PrinterClient.
public sealed class PrinterClient(BleDeviceInfo device)
{
    private readonly BleTransport _transport = new();
    private TaskCompletionSource<byte[]>? _notificationTcs;

    public string DeviceName => _transport.DeviceName;

    public async Task<bool> ConnectAsync()
    {
        if (await _transport.ConnectAsync(device.Address))
        {
            Log.Information($"Successfully connected to {device.Name}");
            return true;
        }

        Log.Error("Connection failed.");
        return false;
    }

    public Task DisconnectAsync()
    {
        _transport.Disconnect();
        Log.Information($"Printer {device.Name} disconnected.");
        return Task.CompletedTask;
    }

    private void OnNotification(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        var data = BleTransport.ToByteArray(args.CharacteristicValue);
        Log.Verbose($"Notification: {Convert.ToHexString(data)}");
        _notificationTcs?.TrySetResult(data);
    }

    private async Task<NiimbotPacket?> SendCommandAsync(RequestCode code, byte[] data, TimeSpan? timeout = null)
    {
        timeout ??= TimeSpan.FromSeconds(10);
        try
        {
            if (!_transport.IsConnected)
                await ConnectAsync();

            _notificationTcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            var packet = new NiimbotPacket((byte)code, data);

            await _transport.StartNotificationAsync(OnNotification);
            await _transport.WriteAsync(packet.ToBytes());
            Log.Debug($"Printer command sent - {code}");

            var completed = await Task.WhenAny(_notificationTcs.Task, Task.Delay(timeout.Value));
            if (completed != _notificationTcs.Task)
            {
                Log.Error($"Timeout occurred for request {code}");
                return null;
            }

            var response = NiimbotPacket.FromBytes(await _notificationTcs.Task);
            await _transport.StopNotificationAsync();
            return response;
        }
        catch (BleException e)
        {
            Log.Error($"An error occurred: {e.Message}");
            return null;
        }
    }

    public async Task WriteRawAsync(NiimbotPacket packet)
    {
        try
        {
            if (!_transport.IsConnected)
                await ConnectAsync();
            await _transport.WriteAsync(packet.ToBytes());
        }
        catch (BleException e)
        {
            Log.Error($"An error occurred: {e.Message}");
        }
    }

    public async Task PrintImageAsync(Image<L8> image, int density = 3, int quantity = 1, int verticalOffset = 0, int horizontalOffset = 0, int threshold = 128, bool dither = false)
    {
        await SetLabelDensityAsync(density);
        await SetLabelTypeAsync(1);
        await StartPrintAsync();
        await StartPagePrintAsync();
        await SetDimensionAsync(image.Height, image.Width);
        await SetQuantityAsync(quantity);

        foreach (var pkt in EncodeImage(image, verticalOffset, horizontalOffset, threshold, dither))
        {
            await WriteRawAsync(pkt);
            await Task.Delay(10);
        }

        while (!await EndPagePrintAsync())
            await Task.Delay(50);

        while (true)
        {
            var status = await GetPrintStatusAsync();
            if (status.Page == quantity)
                break;
            await Task.Delay(100);
        }

        await EndPrintAsync();
    }

    // Port of printer.py's _encode_image(). Mirrors the exact (unusual) bit
    // packing of the original: each row's pixel bits are right-aligned within
    // its ceil(width/8)-byte buffer, so any leftover bits pad the front of the
    // row rather than the end.
    private static IEnumerable<NiimbotPacket> EncodeImage(Image<L8> image, int verticalOffset, int horizontalOffset, int threshold, bool dither)
    {
        var monochrome = dither ? DitherToMonochrome(image, threshold) : ThresholdToMonochrome(image, threshold);

        int width = image.Width;
        int startX = 0;

        if (horizontalOffset <= 0)
        {
            startX = -horizontalOffset;
            width = image.Width - startX;
        }
        else
        {
            width = image.Width + horizontalOffset;
        }

        int height = image.Height + Math.Max(verticalOffset, 0);

        for (int y = 0; y < height; y++)
        {
            var bits = new bool[width];
            int sourceY = y - Math.Max(verticalOffset, 0);

            for (int x = 0; x < width; x++)
            {
                if (sourceY < 0)
                {
                    // Vertical padding row - matches ImageOps.expand(..., fill=1).
                    bits[x] = true;
                    continue;
                }

                if (horizontalOffset > 0 && x < horizontalOffset)
                {
                    // Horizontal padding column - matches ImageOps.expand(..., fill=1).
                    bits[x] = true;
                    continue;
                }

                int sourceX = horizontalOffset > 0 ? x - horizontalOffset : x + startX;
                bits[x] = monochrome[sourceX, sourceY];
            }

            var lineData = PackRowBits(bits);
            var header = new byte[]
            {
                (byte)(y >> 8), (byte)(y & 0xFF), // row index, big-endian
                0, 0, 0,                          // counts - always zero
                1,
            };

            yield return new NiimbotPacket(0x85, [.. header, .. lineData]);
        }
    }

    // Flat cutoff on the inverted grayscale value - no error diffusion, so an
    // already-prepared image (e.g. hand-dithered in an image editor) prints
    // exactly as designed instead of being re-processed on top of that.
    private static bool[,] ThresholdToMonochrome(Image<L8> image, int threshold)
    {
        int width = image.Width;
        int height = image.Height;
        var bits = new bool[width, height];

        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                bits[x, y] = 255 - image[x, y].PackedValue >= threshold;

        return bits;
    }

    // Port of PIL's default convert("1") behavior: Floyd-Steinberg error
    // diffusion over the inverted grayscale image, rather than a flat
    // threshold, so gradients/photos don't come out blocky on the label.
    // Opt-in via --dither; the flat threshold above is the default.
    private static bool[,] DitherToMonochrome(Image<L8> image, int threshold)
    {
        int width = image.Width;
        int height = image.Height;
        var error = new float[width, height];

        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                error[x, y] = 255 - image[x, y].PackedValue;

        var bits = new bool[width, height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                float oldValue = error[x, y];
                bool isSet = oldValue >= threshold;
                bits[x, y] = isSet;
                float diff = oldValue - (isSet ? 255f : 0f);

                if (x + 1 < width)
                    error[x + 1, y] += diff * 7f / 16f;
                if (y + 1 < height)
                {
                    if (x - 1 >= 0)
                        error[x - 1, y + 1] += diff * 3f / 16f;
                    error[x, y + 1] += diff * 5f / 16f;
                    if (x + 1 < width)
                        error[x + 1, y + 1] += diff * 1f / 16f;
                }
            }
        }

        return bits;
    }

    private static byte[] PackRowBits(bool[] bits)
    {
        int width = bits.Length;
        int numBytes = (width + 7) / 8;
        int padBits = numBytes * 8 - width;
        var result = new byte[numBytes];

        for (int i = 0; i < width; i++)
        {
            if (!bits[i])
                continue;

            int bitIndex = padBits + i;
            result[bitIndex / 8] |= (byte)(1 << (7 - bitIndex % 8));
        }

        return result;
    }

    public async Task<object?> GetInfoAsync(InfoKind key)
    {
        var response = await SendCommandAsync(RequestCode.GetInfo, [(byte)key]);
        if (response is null)
            return null;

        return key switch
        {
            InfoKind.DeviceSerial => Convert.ToHexString(response.Data).ToLowerInvariant(),
            InfoKind.SoftVersion => NiimbotPacket.ToInt(response.Data) / 100.0,
            InfoKind.HardVersion => NiimbotPacket.ToInt(response.Data) / 100.0,
            _ => NiimbotPacket.ToInt(response.Data),
        };
    }

    public async Task<RfidInfo?> GetRfidAsync()
    {
        var packet = await SendCommandAsync(RequestCode.GetRfid, [0x01]);
        if (packet is null)
            return null;

        var data = packet.Data;
        if (data[0] == 0)
            return null;

        var uuid = Convert.ToHexString(data[..8]).ToLowerInvariant();
        int idx = 8;

        int barcodeLen = data[idx++];
        var barcode = System.Text.Encoding.UTF8.GetString(data[idx..(idx + barcodeLen)]);
        idx += barcodeLen;

        int serialLen = data[idx++];
        var serial = System.Text.Encoding.UTF8.GetString(data[idx..(idx + serialLen)]);
        idx += serialLen;

        int totalLen = (data[idx] << 8) | data[idx + 1];
        int usedLen = (data[idx + 2] << 8) | data[idx + 3];
        int type = data[idx + 4];

        return new RfidInfo(uuid, barcode, serial, usedLen, totalLen, type);
    }

    public async Task<HeartbeatInfo> HeartbeatAsync()
    {
        var packet = await SendCommandAsync(RequestCode.Heartbeat, [0x01]);
        int? closingState = null, powerLevel = null, paperState = null, rfidReadState = null;

        if (packet is not null)
        {
            var data = packet.Data;
            switch (data.Length)
            {
                case 20:
                    paperState = data[18];
                    rfidReadState = data[19];
                    break;
                case 13:
                    closingState = data[9];
                    powerLevel = data[10];
                    paperState = data[11];
                    rfidReadState = data[12];
                    break;
                case 19:
                    closingState = data[15];
                    powerLevel = data[16];
                    paperState = data[17];
                    rfidReadState = data[18];
                    break;
                case 10:
                    closingState = data[8];
                    powerLevel = data[9];
                    rfidReadState = data[8];
                    break;
                case 9:
                    closingState = data[8];
                    break;
            }
        }

        return new HeartbeatInfo(closingState, powerLevel, paperState, rfidReadState);
    }

    public async Task<bool> SetLabelTypeAsync(int n)
    {
        if (n is < 1 or > 3) throw new ArgumentOutOfRangeException(nameof(n));
        var packet = await SendCommandAsync(RequestCode.SetLabelType, [(byte)n]);
        return packet?.Data[0] != 0;
    }

    public async Task<bool> SetLabelDensityAsync(int n)
    {
        if (n is < 1 or > 5) throw new ArgumentOutOfRangeException(nameof(n));
        var packet = await SendCommandAsync(RequestCode.SetLabelDensity, [(byte)n]);
        return packet?.Data[0] != 0;
    }

    public async Task<bool> StartPrintAsync()
    {
        var packet = await SendCommandAsync(RequestCode.StartPrint, [0x01]);
        return packet?.Data[0] != 0;
    }

    public async Task<bool> EndPrintAsync()
    {
        var packet = await SendCommandAsync(RequestCode.EndPrint, [0x01]);
        return packet?.Data[0] != 0;
    }

    public async Task<bool> StartPagePrintAsync()
    {
        var packet = await SendCommandAsync(RequestCode.StartPagePrint, [0x01]);
        return packet?.Data[0] != 0;
    }

    public async Task<bool> EndPagePrintAsync()
    {
        var packet = await SendCommandAsync(RequestCode.EndPagePrint, [0x01]);
        return packet?.Data[0] != 0;
    }

    public async Task<bool> AllowPrintClearAsync()
    {
        var packet = await SendCommandAsync(RequestCode.AllowPrintClear, [0x01]);
        return packet?.Data[0] != 0;
    }

    public async Task<bool> SetDimensionAsync(int height, int width)
    {
        // Wire protocol packs each field into 2 bytes; a plain cast would silently
        // wrap instead of failing, corrupting the dimension sent to the printer.
        if (height is < 0 or > ushort.MaxValue)
            throw new PrinterException($"Image height {height}px does not fit the printer's 16-bit dimension field (max {ushort.MaxValue}).");
        if (width is < 0 or > ushort.MaxValue)
            throw new PrinterException($"Image width {width}px does not fit the printer's 16-bit dimension field (max {ushort.MaxValue}).");

        var data = new byte[] { (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width };
        var packet = await SendCommandAsync(RequestCode.SetDimension, data);
        return packet?.Data[0] != 0;
    }

    public async Task<bool> SetQuantityAsync(int n)
    {
        var data = new byte[] { (byte)(n >> 8), (byte)n };
        var packet = await SendCommandAsync(RequestCode.SetQuantity, data);
        return packet?.Data[0] != 0;
    }

    public async Task<PrintStatus> GetPrintStatusAsync()
    {
        var packet = await SendCommandAsync(RequestCode.GetPrintStatus, [0x01]);
        if (packet is null)
            return new PrintStatus(0, 0, 0);

        var data = packet.Data;
        int page = (data[0] << 8) | data[1];
        return new PrintStatus(page, data[2], data[3]);
    }
}
