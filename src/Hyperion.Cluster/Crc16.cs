namespace Hyperion.Cluster;

/// <summary>
/// Implements CRC16-CCITT (XMODEM) used by Redis Cluster for hash slot routing.
/// Polynomial: 0x1021, Initial value: 0x0000.
/// </summary>
public static class Crc16
{
    private static readonly ushort[] Table = new ushort[256];

    static Crc16()
    {
        const ushort poly = 0x1021;
        for (int i = 0; i < 256; i++)
        {
            ushort crc = (ushort)(i << 8);
            for (int j = 0; j < 8; j++)
            {
                if ((crc & 0x8000) != 0)
                {
                    crc = (ushort)((crc << 1) ^ poly);
                }
                else
                {
                    crc <<= 1;
                }
            }
            Table[i] = crc;
        }
    }

    public static ushort Compute(string key)
    {
        return Compute(System.Text.Encoding.UTF8.GetBytes(key));
    }

    public static ushort Compute(ReadOnlySpan<byte> bytes)
    {
        ushort crc = 0x0000;
        foreach (byte b in bytes)
        {
            crc = (ushort)((crc << 8) ^ Table[(crc >> 8) ^ b]);
        }
        return crc;
    }
}
