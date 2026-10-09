using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using PhoneCam.Desktop.Video;
using PhoneCam.Streaming.Networking;

string ffmpeg = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "win-x64", "ffmpeg.exe");
if (!File.Exists(ffmpeg))
{
    Console.Error.WriteLine($"FFmpeg not found: {ffmpeg}");
    return 2;
}

byte[] encodedSample = await EncodeSampleAsync(ffmpeg);
if (encodedSample.Length == 0)
{
    Console.Error.WriteLine("FFmpeg did not encode a sample stream.");
    return 1;
}

int port1 = ReservePort();
int port2 = ReservePort();
while (port1 == port2) port2 = ReservePort();
var firstFrame = new TaskCompletionSource<DecodedVideoFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
var secondFrame = new TaskCompletionSource<DecodedVideoFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
await using var decoder1 = new FfmpegH264Decoder(1280, 720, frame => firstFrame.TrySetResult(frame));
await using var decoder2 = new FfmpegH264Decoder(1280, 720, frame => secondFrame.TrySetResult(frame));
await using var receiver1 = new UdpRtpReceiver(port1, decoder1.SubmitAsync);
await using var receiver2 = new UdpRtpReceiver(port2, decoder2.SubmitAsync);
decoder1.Start();
decoder2.Start();
receiver1.Start();
receiver2.Start();

using var sender1 = new UdpClient(AddressFamily.InterNetwork);
using var sender2 = new UdpClient(AddressFamily.InterNetwork);
await SendSampleAsRtpAsync(encodedSample, sender1, sender2, port1, port2);

try
{
    DecodedVideoFrame[] frames = await Task.WhenAll(
        firstFrame.Task.WaitAsync(TimeSpan.FromSeconds(8)),
        secondFrame.Task.WaitAsync(TimeSpan.FromSeconds(8)));
    foreach (var (frame, index) in frames.Select((frame, index) => (frame, index + 1)))
    {
        if (frame.Width != 1280 || frame.Height != 720 || frame.Bgr24.Length != 1280 * 720 * 3)
            throw new InvalidOperationException($"Source {index} returned an invalid frame buffer.");
        if (frame.Bgr24.AsSpan().IndexOfAnyExcept(frame.Bgr24[0]) < 0)
            throw new InvalidOperationException($"Source {index} returned a uniform frame instead of the test pattern.");
    }
    if (receiver1.Snapshot.FrameCount == 0 || receiver2.Snapshot.FrameCount == 0)
        throw new InvalidOperationException("RTP access units were not assembled on both source ports.");
    Console.WriteLine("PASS  H.264 test video travels through two UDP/RTP receivers and two independent decoders into 1280×720 frames");
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FAIL  dual-stream RTP to H.264 preview: {ex.Message}");
    Console.Error.WriteLine($"Source 1 FFmpeg: {decoder1.LastError ?? "no error reported"}");
    Console.Error.WriteLine($"Source 2 FFmpeg: {decoder2.LastError ?? "no error reported"}");
    return 1;
}

return 0;

static async Task SendSampleAsRtpAsync(byte[] annexB, UdpClient sender1, UdpClient sender2, int port1, int port2)
{
    ushort sequence1 = 0;
    ushort sequence2 = 0;
    uint timestamp = 0;
    var destination1 = new IPEndPoint(IPAddress.Loopback, port1);
    var destination2 = new IPEndPoint(IPAddress.Loopback, port2);

    foreach (byte[] nal in SplitAnnexB(annexB))
    {
        foreach (byte[] packet in PacketizeNal(nal, sequence1, timestamp))
        {
            await Task.WhenAll(sender1.SendAsync(packet, destination1).AsTask(), sender2.SendAsync(packetizeWithSequence(packet, ref sequence2), destination2).AsTask());
            sequence1++;
            await Task.Delay(1);
        }
        timestamp = unchecked(timestamp + 3000);
    }

    static byte[] packetizeWithSequence(byte[] packet, ref ushort sequence)
    {
        byte[] copy = (byte[])packet.Clone();
        copy[2] = (byte)(sequence >> 8);
        copy[3] = (byte)sequence;
        sequence++;
        return copy;
    }
}

static IEnumerable<byte[]> PacketizeNal(byte[] nal, ushort firstSequence, uint timestamp)
{
    const int maxDatagramSize = 1200;
    const int headerSize = 12;
    const int maxSingleNal = maxDatagramSize - headerSize;
    ushort sequence = firstSequence;
    if (nal.Length <= maxSingleNal)
    {
        yield return CreateRtp(sequence, timestamp, true, nal);
        yield break;
    }

    byte indicator = (byte)((nal[0] & 0xe0) | 28);
    byte type = (byte)(nal[0] & 0x1f);
    int offset = 1;
    const int fragmentBodySize = maxDatagramSize - headerSize - 2;
    while (offset < nal.Length)
    {
        int count = Math.Min(fragmentBodySize, nal.Length - offset);
        bool start = offset == 1;
        bool end = offset + count == nal.Length;
        byte fuHeader = (byte)(type | (start ? 0x80 : 0) | (end ? 0x40 : 0));
        byte[] payload = new byte[count + 2];
        payload[0] = indicator;
        payload[1] = fuHeader;
        nal.AsSpan(offset, count).CopyTo(payload.AsSpan(2));
        yield return CreateRtp(sequence++, timestamp, end, payload);
        offset += count;
    }
}

static byte[] CreateRtp(ushort sequence, uint timestamp, bool marker, byte[] payload)
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
    packet[8] = 0x50;
    packet[9] = 0x43;
    packet[10] = 0x41;
    packet[11] = 0x4d;
    payload.CopyTo(packet, 12);
    return packet;
}

static IEnumerable<byte[]> SplitAnnexB(byte[] stream)
{
    int offset = FindStartCode(stream, 0, out int codeLength);
    while (offset >= 0)
    {
        int start = offset + codeLength;
        int next = FindStartCode(stream, start, out int nextCodeLength);
        int end = next >= 0 ? next : stream.Length;
        while (end > start && stream[end - 1] == 0) end--;
        if (end > start) yield return stream.AsSpan(start, end - start).ToArray();
        offset = next;
        codeLength = nextCodeLength;
    }
}

static int FindStartCode(byte[] data, int from, out int length)
{
    for (int i = from; i + 2 < data.Length; i++)
    {
        if (data[i] != 0 || data[i + 1] != 0) continue;
        if (data[i + 2] == 1) { length = 3; return i; }
        if (i + 3 < data.Length && data[i + 2] == 0 && data[i + 3] == 1) { length = 4; return i; }
    }
    length = 0;
    return -1;
}

static int ReservePort()
{
    using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
    return ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
}

static async Task<byte[]> EncodeSampleAsync(string ffmpeg)
{
    var startInfo = new ProcessStartInfo(ffmpeg)
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };
    foreach (string argument in new[]
    {
        "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc=size=320x240:rate=30",
        "-frames:v", "8", "-pix_fmt", "yuv420p", "-c:v", "libopenh264", "-g", "8", "-f", "h264", "pipe:1"
    }) startInfo.ArgumentList.Add(argument);

    using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start FFmpeg test encoder.");
    using var sample = new MemoryStream();
    Task copy = process.StandardOutput.BaseStream.CopyToAsync(sample);
    Task<string> stderr = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
    await copy;
    string error = await stderr;
    if (process.ExitCode != 0)
        throw new InvalidOperationException($"FFmpeg test encoding failed: {error}");
    return sample.ToArray();
}
