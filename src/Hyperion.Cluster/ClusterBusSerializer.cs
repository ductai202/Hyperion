using System;
using System.IO;
using System.Text;
using System.Collections;

namespace Hyperion.Cluster;

public enum ClusterMessageType : ushort
{
    Ping = 0,
    Pong = 1,
    Meet = 2,
    Fail = 3,
    Publish = 4
}

public class GossipEntry
{
    public string NodeId { get; set; } = string.Empty;
    public uint PingSent { get; set; }
    public uint PongReceived { get; set; }
    public string Ip { get; set; } = string.Empty;
    public ushort Port { get; set; }
    public ushort CPort { get; set; }
    public ClusterNodeFlags Flags { get; set; }
}

public class ClusterMessage
{
    public uint TotalLength { get; set; }
    public ushort Version { get; set; } = 1;
    public ushort Port { get; set; }
    public ClusterMessageType Type { get; set; }
    public ushort Count { get; set; }
    public ulong CurrentEpoch { get; set; }
    public ulong ConfigEpoch { get; set; }
    public ulong Offset { get; set; }
    public string SenderNodeId { get; set; } = string.Empty;
    public BitArray MySlots { get; set; } = new BitArray(16384);
    public string SlaveOf { get; set; } = string.Empty;
    public string MyIp { get; set; } = string.Empty;
    public ushort CPort { get; set; }
    public ClusterNodeFlags Flags { get; set; }
    public ClusterStatus State { get; set; }
    
    public GossipEntry[] GossipEntries { get; set; } = Array.Empty<GossipEntry>();
}

public static class ClusterBusSerializer
{
    private static readonly byte[] Signature = Encoding.ASCII.GetBytes("RCmb");

    public static byte[] Serialize(ClusterMessage msg)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        // Header: fixed size
        writer.Write(Signature); // 4 bytes
        writer.Write((uint)0); // placeholder for total length
        writer.Write(msg.Version); // 2 bytes
        writer.Write(msg.Port); // 2 bytes
        writer.Write((ushort)msg.Type); // 2 bytes
        writer.Write((ushort)msg.GossipEntries.Length); // count, 2 bytes
        writer.Write(msg.CurrentEpoch); // 8 bytes
        writer.Write(msg.ConfigEpoch); // 8 bytes
        writer.Write(msg.Offset); // 8 bytes
        
        WriteFixedString(writer, msg.SenderNodeId, 40);
        
        // myslots: 16384 bits = 2048 bytes
        byte[] slotsBytes = new byte[2048];
        msg.MySlots.CopyTo(slotsBytes, 0);
        writer.Write(slotsBytes);
        
        WriteFixedString(writer, msg.SlaveOf, 40);
        WriteFixedString(writer, msg.MyIp, 32);
        
        writer.Write(msg.CPort); // 2 bytes
        writer.Write((ushort)msg.Flags); // 2 bytes
        writer.Write((byte)(msg.State == ClusterStatus.Ok ? 0 : 1)); // 1 byte

        // Align header or pad if necessary. Let's say header is precisely this length.
        // sum = 4+4+2+2+2+2+8+8+8 + 40 + 2048 + 40 + 32 + 2+2+1 = 2205 bytes
        // (Redis header actually has some padding, but we're keeping it simple and custom for Hyperion)

        // Gossip entries
        foreach (var entry in msg.GossipEntries)
        {
            WriteFixedString(writer, entry.NodeId, 40);
            writer.Write(entry.PingSent); // 4 bytes
            writer.Write(entry.PongReceived); // 4 bytes
            WriteFixedString(writer, entry.Ip, 32);
            writer.Write(entry.Port); // 2 bytes
            writer.Write(entry.CPort); // 2 bytes
            writer.Write((ushort)entry.Flags); // 2 bytes
        }

        uint totalLength = (uint)ms.Length;
        ms.Position = 4;
        writer.Write(totalLength);

        return ms.ToArray();
    }

    public static ClusterMessage? Deserialize(byte[] data)
    {
        if (data.Length < 2205) return null; // Minimum header size
        
        using var ms = new MemoryStream(data);
        using var reader = new BinaryReader(ms);

        var sig = reader.ReadBytes(4);
        if (sig[0] != 'R' || sig[1] != 'C' || sig[2] != 'm' || sig[3] != 'b') return null;

        var msg = new ClusterMessage();
        msg.TotalLength = reader.ReadUInt32();
        msg.Version = reader.ReadUInt16();
        msg.Port = reader.ReadUInt16();
        msg.Type = (ClusterMessageType)reader.ReadUInt16();
        msg.Count = reader.ReadUInt16();
        msg.CurrentEpoch = reader.ReadUInt64();
        msg.ConfigEpoch = reader.ReadUInt64();
        msg.Offset = reader.ReadUInt64();
        msg.SenderNodeId = ReadFixedString(reader, 40);
        
        var slotsBytes = reader.ReadBytes(2048);
        msg.MySlots = new BitArray(slotsBytes);
        
        msg.SlaveOf = ReadFixedString(reader, 40);
        msg.MyIp = ReadFixedString(reader, 32);
        
        msg.CPort = reader.ReadUInt16();
        msg.Flags = (ClusterNodeFlags)reader.ReadUInt16();
        msg.State = reader.ReadByte() == 0 ? ClusterStatus.Ok : ClusterStatus.Fail;

        var entries = new GossipEntry[msg.Count];
        for (int i = 0; i < msg.Count; i++)
        {
            entries[i] = new GossipEntry
            {
                NodeId = ReadFixedString(reader, 40),
                PingSent = reader.ReadUInt32(),
                PongReceived = reader.ReadUInt32(),
                Ip = ReadFixedString(reader, 32),
                Port = reader.ReadUInt16(),
                CPort = reader.ReadUInt16(),
                Flags = (ClusterNodeFlags)reader.ReadUInt16()
            };
        }
        msg.GossipEntries = entries;

        return msg;
    }

    private static void WriteFixedString(BinaryWriter writer, string value, int length)
    {
        byte[] bytes = new byte[length];
        if (!string.IsNullOrEmpty(value))
        {
            var strBytes = Encoding.UTF8.GetBytes(value);
            Array.Copy(strBytes, 0, bytes, 0, Math.Min(strBytes.Length, length));
        }
        writer.Write(bytes);
    }

    private static string ReadFixedString(BinaryReader reader, int length)
    {
        byte[] bytes = reader.ReadBytes(length);
        int nullIdx = Array.IndexOf(bytes, (byte)0);
        int strLen = nullIdx == -1 ? length : nullIdx;
        return Encoding.UTF8.GetString(bytes, 0, strLen);
    }
}
