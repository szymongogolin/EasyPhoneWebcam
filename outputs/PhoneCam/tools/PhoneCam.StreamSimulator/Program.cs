using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

string host = Option("--host", "127.0.0.1");
int port = int.Parse(Option("--port", "5004"));
int fps = int.Parse(Option("--fps", "60"));
int seconds = int.Parse(Option("--seconds", "15"));
if (port is < 1 or > 65535 || fps is < 1 or > 240 || seconds < 1)
{
    Console.Error.WriteLine("Użycie: PhoneCam.StreamSimulator [--host IP] [--port 5004|5006] [--fps 1..240] [--seconds N]");
    return 2;
}

using var udp = new UdpClient(AddressFamily.InterNetwork);
var endpoint = new IPEndPoint(IPAddress.Parse(host), port);
ushort sequence = 0;
uint timestamp = 0;
long frames = 0;
long targetFrames = (long)fps * seconds;
var timer = Stopwatch.StartNew();
Console.WriteLine($"Wysyłam syntetyczne pakiety RTP do {host}:{port} z częstotliwością {fps} fps przez {seconds} s. To nie jest dekodowalny obraz.");

while (frames < targetFrames)
{
    // Single-NAL RTP packet. The short NAL body exists only to exercise network
    // reception and frame assembly; it intentionally is not a decodable H.264 picture.
    byte[] packet = new byte[14];
    packet[0] = 0x80; // RTP version 2
    packet[1] = 0xe0; // marker + dynamic payload type 96
    packet[2] = (byte)(sequence >> 8);
    packet[3] = (byte)sequence;
    packet[4] = (byte)(timestamp >> 24);
    packet[5] = (byte)(timestamp >> 16);
    packet[6] = (byte)(timestamp >> 8);
    packet[7] = (byte)timestamp;
    packet[8] = 0x50; // fixed SSRC for this local simulator
    packet[9] = 0x43;
    packet[10] = 0x41;
    packet[11] = 0x4d;
    packet[12] = 0x61; // H.264 non-IDR NAL type
    packet[13] = (byte)frames;
    await udp.SendAsync(packet, endpoint);

    sequence++;
    timestamp = unchecked(timestamp + (uint)(90_000 / fps));
    frames++;
    TimeSpan next = TimeSpan.FromSeconds(frames / (double)fps) - timer.Elapsed;
    if (next > TimeSpan.Zero)
        await Task.Delay(next);
}

Console.WriteLine($"Wysłano {frames:N0} pakietów/klatek testowych.");
return 0;

string Option(string name, string fallback)
{
    int index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback;
}
