using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using PhoneCam.Desktop.Hosting;

namespace PhoneCam.Desktop.Video;

/// <summary>
/// Decodes a network stream (RTSP from MediaMTX) into fixed-size landscape BGR24 frames: the source is
/// rotated by <paramref name="rotation"/> degrees counter-clockwise (as reported by the phone), then
/// centre-cropped to the output aspect ratio, so a portrait source fills the frame instead of getting bars.
/// Frames are handed over through a lock-free triple buffer: the consumer always gets the newest complete
/// frame, older ones are dropped, and nothing tears or queues up.
/// </summary>
public sealed partial class FfmpegStreamDecoder(string inputUrl, int width, int height, int rotation = 0) : IAsyncDisposable
{
    /// <summary>FFmpeg filter chain; crop expressions are re-evaluated when the source resolution changes mid-stream.</summary>
    internal static string BuildFilter(int width, int height, int rotation)
    {
        string rotate = rotation switch
        {
            90 => "transpose=cclock,",
            180 => "hflip,vflip,",
            270 => "transpose=clock,",
            0 => "",
            _ => throw new ArgumentOutOfRangeException(nameof(rotation), rotation, "Obsługiwane są obroty 0, 90, 180 i 270°.")
        };
        return $"{rotate}crop='min(iw,ih*{width}/{height})':'min(ih,iw*{height}/{width})',scale={width}:{height},setsar=1";
    }

    private readonly CancellationTokenSource _stop = new();
    private Process? _process;
    private Task? _readTask;
    private Task? _errorTask;
    private DecodedVideoFrame? _ready;
    private DecodedVideoFrame? _spare;
    private long _decodedFrames;
    private bool _disposing;

    public int Rotation => rotation;
    public string? LastError { get; private set; }
    public (int Width, int Height)? SourceSize { get; private set; }
    public long DecodedFrames => Interlocked.Read(ref _decodedFrames);

    /// <summary>Raised on a background thread when FFmpeg exits on its own (stream ended or failed).</summary>
    public event Action? Exited;

    /// <summary>
    /// Raised on the reader thread for every frame, before it becomes visible to <see cref="TakeLatest"/>.
    /// Handlers must copy what they need and return quickly; the buffer is reused afterwards.
    /// </summary>
    public event Action<DecodedVideoFrame>? FrameDecoded;

    public void Start()
    {
        var startInfo = new ProcessStartInfo(FfmpegH264Decoder.FindFfmpeg())
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in new[]
        {
            "-hide_banner", "-nostdin", "-nostats", "-loglevel", "info",
            "-hwaccel", "auto", "-fflags", "nobuffer", "-flags", "low_delay",
            "-rtsp_transport", "tcp", "-probesize", "500000", "-analyzeduration", "500000",
            "-i", inputUrl,
            "-an", "-sn", "-dn",
            "-vf", BuildFilter(width, height, rotation),
            "-fps_mode", "passthrough", "-pix_fmt", "bgr24", "-f", "rawvideo", "pipe:1"
        }) startInfo.ArgumentList.Add(argument);

        _process = Process.Start(startInfo) ?? throw new InvalidOperationException("Nie udało się uruchomić FFmpeg.");
        ChildProcessJob.Add(_process);
        _readTask = Task.Run(() => ReadFramesAsync(_stop.Token));
        _errorTask = Task.Run(() => ReadErrorsAsync(_stop.Token));
    }

    /// <summary>Takes the newest frame, if a new one arrived. Hand it back with <see cref="Return"/> after use.</summary>
    public DecodedVideoFrame? TakeLatest() => Interlocked.Exchange(ref _ready, null);

    public void Return(DecodedVideoFrame frame) => Interlocked.Exchange(ref _spare, frame);

    private async Task ReadFramesAsync(CancellationToken cancellationToken)
    {
        Stream output = _process!.StandardOutput.BaseStream;
        DecodedVideoFrame back = NewFrame();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await output.ReadExactlyAsync(back.Bgr24, cancellationToken);
                Interlocked.Increment(ref _decodedFrames);
                FrameDecoded?.Invoke(back);
                back = Interlocked.Exchange(ref _ready, back)        // unconsumed frame becomes the next back buffer
                    ?? Interlocked.Exchange(ref _spare, null)
                    ?? NewFrame();
            }
        }
        catch (OperationCanceledException) { }
        catch (EndOfStreamException) { }
        catch (IOException ex) when (!cancellationToken.IsCancellationRequested)
        {
            LastError = $"Błąd odczytu dekodera: {ex.Message}";
        }
        if (!_disposing) Exited?.Invoke();
    }

    private DecodedVideoFrame NewFrame() => new(new byte[checked(width * height * 3)], width, height);

    [GeneratedRegex(@"Stream #\d+:\d+.*Video: .*?, (\d{2,5})x(\d{2,5})")]
    private static partial Regex VideoStreamLine();

    private async Task ReadErrorsAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                string? line = await _process!.StandardError.ReadLineAsync(cancellationToken);
                if (line is null) break;
                if (SourceSize is null && VideoStreamLine().Match(line) is { Success: true } match)
                    SourceSize = (int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value));
                else if (line.Contains("error", StringComparison.OrdinalIgnoreCase) || line.Contains("failed", StringComparison.OrdinalIgnoreCase))
                    LastError = line;
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    public async ValueTask DisposeAsync()
    {
        _disposing = true;
        _stop.Cancel();
        if (_process is { } process)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (TimeoutException) { }
        }
        if (_readTask is not null) await _readTask;
        if (_errorTask is not null) await _errorTask;
        _process?.Dispose();
        _process = null;
        _stop.Dispose();
    }
}
