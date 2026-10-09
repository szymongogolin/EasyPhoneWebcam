using System.Windows.Threading;

namespace PhoneCam.Desktop.Video;

/// <summary>
/// Turns one slot's RTSP stream into 1280×720 landscape frames for its consumers: the slot's virtual camera
/// (for as long as PhoneCam runs) and the preview window (while it is open). Owns the decoder: started when
/// the slot publishes, restarted when the phone turns or FFmpeg drops. Every decoded frame goes to the camera
/// straight from the decoder thread; without live frames the camera shows a "no signal" card.
/// Public members are called on the UI thread.
/// </summary>
internal sealed class SlotFeed : IAsyncDisposable
{
    private const int PumpIntervalMs = 100;          // "no signal" card at 10 fps
    private const long NoSignalAfterMs = 500;
    private const int CameraRetryTicks = 20;         // retry creating the camera every 2 s

    private readonly string _streamUrl;
    private readonly int _cameraIndex;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly byte[] _noSignal;
    private readonly Timer _pump;
    private VirtualCamera? _camera;
    private FfmpegStreamDecoder? _decoder;
    private bool _publishing;
    private bool _disposed;
    private int _rotation;
    private int _pumpTicks;
    private long _lastLiveFrameMs;
    private long _framesFromPreviousDecoders;

    /// <param name="cameraIndex">Index in PhoneCam.VirtualCamera.dll's camera table (0 = iPhone).</param>
    public SlotFeed(string streamUrl, int cameraIndex, string cameraName)
    {
        _streamUrl = streamUrl;
        _cameraIndex = cameraIndex;
        _noSignal = NoSignalCard.Render(VirtualCamera.Width, VirtualCamera.Height, cameraName, "Brak sygnału · otwórz stronę kamery na telefonie");
        _camera = VirtualCamera.TryCreate(cameraIndex);
        _pump = new Timer(_ => Pump(), null, 0, PumpIntervalMs);
    }

    /// <summary>Current decoder for the preview (<see cref="FfmpegStreamDecoder.TakeLatest"/>); null while idle.</summary>
    public FfmpegStreamDecoder? Decoder => _decoder;

    /// <summary>What the preview should say while no frames show; raised on the UI thread.</summary>
    public string Status { get; private set; } = "Oczekiwanie na obraz…";
    public event Action? StatusChanged;

    public int Rotation => _rotation;
    public long DecodedFrames => _framesFromPreviousDecoders + (_decoder?.DecodedFrames ?? 0);
    public (int Width, int Height)? SourceSize => _decoder?.SourceSize;

    /// <summary>The camera exists (DLL present, frame buffer created). False until a retry succeeds.</summary>
    public bool CameraAvailable => Volatile.Read(ref _camera) is not null;
    public bool CameraInUse => Volatile.Read(ref _camera)?.InUse == true;

    public void SetPublishing(bool publishing)
    {
        if (publishing == _publishing || _disposed) return;
        _publishing = publishing;
        if (publishing)
            StartDecoder();
        else
        {
            _ = StopDecoderAsync();
            SetStatus("Brak sygnału · telefon przestał nadawać");
        }
    }

    /// <summary>The rotation is baked into the FFmpeg filter, so a change restarts the decoder (rare: the phone was turned).</summary>
    public async void SetRotation(int rotation)
    {
        if (rotation == _rotation || _disposed) return;
        _rotation = rotation;
        if (_decoder is null) return;
        await StopDecoderAsync();
        if (_publishing && !_disposed && _decoder is null)
        {
            StartDecoder();
            SetStatus("Obracanie obrazu…");
        }
    }

    private void StartDecoder()
    {
        SetStatus("Łączenie z obrazem…");
        var decoder = new FfmpegStreamDecoder(_streamUrl, VirtualCamera.Width, VirtualCamera.Height, _rotation);
        decoder.FrameDecoded += OnFrameDecoded;
        decoder.Exited += () => _dispatcher.InvokeAsync(() => RestartAfterFailureAsync(decoder));
        try
        {
            decoder.Start();
            _decoder = decoder;
        }
        catch (Exception ex)
        {
            SetStatus($"Nie można uruchomić dekodera: {ex.Message}");
        }
    }

    // Decoder thread: hand the frame to the camera immediately (a copy into shared memory, no queue).
    private void OnFrameDecoded(DecodedVideoFrame frame)
    {
        Volatile.Read(ref _camera)?.Send(frame.Bgr24);
        Volatile.Write(ref _lastLiveFrameMs, Environment.TickCount64);
    }

    private async Task RestartAfterFailureAsync(FfmpegStreamDecoder failed)
    {
        if (!ReferenceEquals(failed, _decoder)) return;
        await StopDecoderAsync();
        if (!_publishing || _disposed) return;
        SetStatus("Ponowne łączenie z obrazem…");
        await Task.Delay(TimeSpan.FromSeconds(1));
        if (_publishing && !_disposed && _decoder is null)
            StartDecoder();
    }

    private async Task StopDecoderAsync()
    {
        if (_decoder is not { } decoder) return;
        _decoder = null;
        _framesFromPreviousDecoders += decoder.DecodedFrames;
        decoder.FrameDecoded -= OnFrameDecoded;
        await decoder.DisposeAsync();
    }

    // Timer thread: keeps the camera alive without live frames and recreates it when it could not be created
    // (an app still held the previous session's frame buffer open).
    private void Pump()
    {
        VirtualCamera? camera = Volatile.Read(ref _camera);
        if (camera is null)
        {
            if (_pumpTicks++ % CameraRetryTicks == 0 && VirtualCamera.TryCreate(_cameraIndex) is { } created
                && Interlocked.CompareExchange(ref _camera, created, null) is not null)
                created.Dispose();
            return;
        }
        if (Environment.TickCount64 - Volatile.Read(ref _lastLiveFrameMs) > NoSignalAfterMs)
            camera.Send(_noSignal);
    }

    private void SetStatus(string status)
    {
        Status = status;
        StatusChanged?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _pump.DisposeAsync();
        await StopDecoderAsync();
        Interlocked.Exchange(ref _camera, null)?.Dispose();
    }
}
