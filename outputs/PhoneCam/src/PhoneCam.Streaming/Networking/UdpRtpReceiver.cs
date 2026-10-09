using System.Net;
using System.Net.Sockets;

namespace PhoneCam.Streaming.Networking;

public sealed class UdpRtpReceiver(int port, Func<ReadOnlyMemory<byte>, ValueTask>? onAccessUnit = null) : IAsyncDisposable
{
    private readonly RtpH264Depacketizer _depacketizer = new();
    private UdpClient? _client;
    private CancellationTokenSource? _stop;
    private Task? _receiveTask;
    private long _packetCount;
    private long _bytesReceived;
    private long _lastPacketTicks;
    private long _frameCount;
    private long _lostPackets;

    public UdpReceiverSnapshot Snapshot => new(
        Interlocked.Read(ref _packetCount),
        Interlocked.Read(ref _bytesReceived),
        Interlocked.Read(ref _lastPacketTicks) is long ticks && ticks > 0 ? new DateTime(ticks, DateTimeKind.Utc) : null,
        Interlocked.Read(ref _frameCount),
        Interlocked.Read(ref _lostPackets));

    public void Start()
    {
        if (_client is not null)
            throw new InvalidOperationException("UDP odbiornik został już uruchomiony.");

        _stop = new CancellationTokenSource();
        _client = new UdpClient(new IPEndPoint(IPAddress.Any, port));
        _receiveTask = ReceiveLoopAsync(_stop.Token);
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                UdpReceiveResult packet = await _client!.ReceiveAsync(cancellationToken);
                Interlocked.Increment(ref _packetCount);
                Interlocked.Add(ref _bytesReceived, packet.Buffer.Length);
                Interlocked.Exchange(ref _lastPacketTicks, DateTime.UtcNow.Ticks);
                byte[]? accessUnit = _depacketizer.Push(packet.Buffer);
                if (accessUnit is not null && onAccessUnit is not null)
                    await onAccessUnit(accessUnit);
                Interlocked.Exchange(ref _frameCount, _depacketizer.CompletedAccessUnits);
                Interlocked.Exchange(ref _lostPackets, _depacketizer.LostPackets);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (SocketException) when (cancellationToken.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (_stop is null)
            return;

        _stop.Cancel();
        _client?.Dispose();
        if (_receiveTask is not null)
            await _receiveTask;
        _stop.Dispose();
        _stop = null;
        _client = null;
        _receiveTask = null;
    }
}

public readonly record struct UdpReceiverSnapshot(long PacketCount, long BytesReceived, DateTime? LastPacketUtc, long FrameCount, long LostPackets);
