namespace Niimbot;

// Port of packet.py - frames commands/responses exchanged with the printer:
// 0x55 0x55 <type> <len> <data...> <checksum> 0xAA 0xAA
public sealed class NiimbotPacket
{
    public byte Type { get; }
    public byte[] Data { get; }

    public NiimbotPacket(byte type, byte[] data)
    {
        Type = type;
        Data = data;
    }

    public static NiimbotPacket FromBytes(byte[] pkt)
    {
        if (pkt.Length < 7 || pkt[0] != 0x55 || pkt[1] != 0x55)
            throw new PrinterException("Invalid packet header.");
        if (pkt[^2] != 0xAA || pkt[^1] != 0xAA)
            throw new PrinterException("Invalid packet footer.");

        byte type = pkt[2];
        byte len = pkt[3];
        var data = pkt[4..(4 + len)];

        byte checksum = (byte)(type ^ len);
        foreach (var b in data)
            checksum ^= b;

        if (checksum != pkt[^3])
            throw new PrinterException("Packet checksum mismatch.");

        return new NiimbotPacket(type, data);
    }

    public byte[] ToBytes()
    {
        byte checksum = (byte)(Type ^ Data.Length);
        foreach (var b in Data)
            checksum ^= b;

        var result = new byte[Data.Length + 7];
        result[0] = 0x55;
        result[1] = 0x55;
        result[2] = Type;
        result[3] = (byte)Data.Length;
        Data.CopyTo(result, 4);
        result[^3] = checksum;
        result[^2] = 0xAA;
        result[^1] = 0xAA;
        return result;
    }

    // Equivalent of packet_to_int(): big-endian bytes -> integer.
    public static long ToInt(byte[] data)
    {
        long value = 0;
        foreach (var b in data)
            value = (value << 8) | b;
        return value;
    }

    public override string ToString() => $"<NiimbotPacket type={Type} data={Convert.ToHexString(Data)}>";
}
