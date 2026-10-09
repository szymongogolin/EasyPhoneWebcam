using System.Net;
using System.Net.Sockets;
using PhoneCam.Streaming.Networking;

var checks = new (string Name, Action Run)[]
{
    ("single NAL becomes Annex-B access unit", SingleNal),
    ("STAP-A yields multiple NAL units", StapA),
    ("FU-A fragments are reassembled", FuA),
    ("packet loss drops only the damaged access unit", LossRecovery),
    ("RTP sequence wraps from 65535 to 0", SequenceWrap),
    ("malformed RTP header is ignored", MalformedHeader)
};

int failures = 0;
foreach (var check in checks)
{
    try
    {
        check.Run();
        Console.WriteLine($"PASS  {check.Name}");
    }
    catch (Exception ex)
    {
        failures++;
        Console.WriteLine($"FAIL  {check.Name}: {ex.Message}");
    }
}

try
{
    await DualUdpReceive();
    Console.WriteLine("PASS  two independent UDP receivers accept streams concurrently");
}
catch (Exception ex)
{
    failures++;
    Console.WriteLine($"FAIL  two independent UDP receivers accept streams concurrently: {ex.Message}");
}

Console.WriteLine($"{checks.Length + 1 - failures}/{checks.Length + 1} streaming checks passed.");
return failures == 0 ? 0 : 1;

static void SingleNal()
{
    var receiver = new RtpH264Depacketizer();
    byte[]? frame = receiver.Push(Rtp(1, 90_000, true, [0x65, 0x11, 0x22]));
    EqualBytes([0, 0, 0, 1, 0x65, 0x11, 0x22], frame);
    Equal(1L, receiver.CompletedAccessUnits);
}

static void StapA()
{
    var receiver = new RtpH264Depacketizer();
    byte[] stap = [24, 0, 2, 0x67, 0xaa, 0, 2, 0x68, 0xbb];
    byte[]? frame = receiver.Push(Rtp(7, 1, true, stap));
    EqualBytes([0, 0, 0, 1, 0x67, 0xaa, 0, 0, 0, 1, 0x68, 0xbb], frame);
}

static void FuA()
{
    var receiver = new RtpH264Depacketizer();
    byte[]? first = receiver.Push(Rtp(10, 42, false, [0x7c, 0x85, 0xaa, 0xbb]));
    if (first is not null) throw new InvalidOperationException("FU-A frame completed before the end fragment.");
    byte[]? frame = receiver.Push(Rtp(11, 42, true, [0x7c, 0x45, 0xcc]));
    EqualBytes([0, 0, 0, 1, 0x65, 0xaa, 0xbb, 0xcc], frame);
}

static void LossRecovery()
{
    var receiver = new RtpH264Depacketizer();
    receiver.Push(Rtp(20, 100, false, [0x61, 0x01]));
    byte[]? damaged = receiver.Push(Rtp(22, 100, true, [0x61, 0x02]));
    if (damaged is not null) throw new InvalidOperationException("Damaged access unit was emitted.");
    Equal(1L, receiver.LostPackets);

    byte[]? recovered = receiver.Push(Rtp(23, 200, true, [0x61, 0x03]));
    EqualBytes([0, 0, 0, 1, 0x61, 0x03], recovered);
}

static void SequenceWrap()
{
    var receiver = new RtpH264Depacketizer();
    receiver.Push(Rtp(ushort.MaxValue, 100, true, [0x61, 0x01]));
    receiver.Push(Rtp(0, 200, true, [0x61, 0x02]));
    Equal(0L, receiver.LostPackets);
    Equal(2L, receiver.CompletedAccessUnits);
}

static void MalformedHeader()
{
    var receiver = new RtpH264Depacketizer();
    if (receiver.Push([0x80, 0x60, 0]) is not null)
        throw new InvalidOperationException("Short packet was accepted.");
    Equal(0L, receiver.CompletedAccessUnits);
}

static byte[] Rtp(ushort sequence, uint timestamp, bool marker, byte[] payload)
{
    byte[] packet = new byte[12 + payload.Length];
    packet[0] = 0x80;
    packet[1] = (byte)(96 | (marker ? 0x80 : 0));
    packet[2] = (byte)(sequence >> 8);
    packet[3] = (byte)sequence;
    packet[4] = (byte)(timestamp >> 24);
    packet[5] = (byte)(timestamp >> 16);
    packet[6] = (byte)(timestamp >> 8);
    packet[7] = (byte)timestamp;
    payload.CopyTo(packet, 12);
    return packet;
}

static void Equal<T>(T expected, T actual) where T : notnull
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected {expected}, got {actual}.");
}

static void EqualBytes(byte[] expected, byte[]? actual)
{
    if (actual is null || !expected.AsSpan().SequenceEqual(actual))
        throw new InvalidOperationException($"Expected [{Convert.ToHexString(expected)}], got [{(actual is null ? "null" : Convert.ToHexString(actual))}].");
}

static async Task DualUdpReceive()
{
    int port1 = ReserveEphemeralPort();
    int port2 = ReserveEphemeralPort();
    while (port1 == port2) port2 = ReserveEphemeralPort();

    await using var receiver1 = new UdpRtpReceiver(port1);
    await using var receiver2 = new UdpRtpReceiver(port2);
    receiver1.Start();
    receiver2.Start();
    using var sender1 = new UdpClient(AddressFamily.InterNetwork);
    using var sender2 = new UdpClient(AddressFamily.InterNetwork);
    var destination1 = new IPEndPoint(IPAddress.Loopback, port1);
    var destination2 = new IPEndPoint(IPAddress.Loopback, port2);

    for (ushort i = 0; i < 20; i++)
    {
        await Task.WhenAll(
            sender1.SendAsync(Rtp(i, (uint)i * 1500, true, [0x61, 0x01]), destination1).AsTask(),
            sender2.SendAsync(Rtp(i, (uint)i * 3000, true, [0x61, 0x02]), destination2).AsTask());
    }

    DateTime deadline = DateTime.UtcNow.AddSeconds(5);
    while ((receiver1.Snapshot.FrameCount < 20 || receiver2.Snapshot.FrameCount < 20) && DateTime.UtcNow < deadline)
        await Task.Delay(10);

    Equal(20L, receiver1.Snapshot.PacketCount);
    Equal(20L, receiver2.Snapshot.PacketCount);
    Equal(20L, receiver1.Snapshot.FrameCount);
    Equal(20L, receiver2.Snapshot.FrameCount);
    Equal(0L, receiver1.Snapshot.LostPackets);
    Equal(0L, receiver2.Snapshot.LostPackets);
}

static int ReserveEphemeralPort()
{
    using var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
    return ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
}
