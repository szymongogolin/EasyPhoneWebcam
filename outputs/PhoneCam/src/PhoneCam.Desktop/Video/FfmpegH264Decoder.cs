using System.Diagnostics;
using System.IO;

namespace PhoneCam.Desktop.Video;

public sealed record DecodedVideoFrame(byte[] Bgr24, int Width, int Height);

/// <summary>Low-buffer H.264 Annex-B decoder. Each camera slot owns one process and one input pipe.</summary>
public sealed class FfmpegH264Decoder(int width, int height, Action<DecodedVideoFrame> onFrame) : IAsyncDisposable
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private Process? _process;
    private Task? _readTask;
    private Task? _errorTask;
    private long _decodedFrames;

    public string? LastError { get; private set; }
    public long DecodedFrames => Interlocked.Read(ref _decodedFrames);

    public void Start()
    {
        string executable = FindFfmpeg();
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in new[]
        {
            "-hide_banner", "-loglevel", "warning", "-hwaccel", "auto", "-flags", "low_delay",
            "-probesize", "32768", "-analyzeduration", "100000", "-f", "h264", "-i", "pipe:0",
            "-an", "-sn", "-dn", "-vf", $"scale={width}:{height}", "-fps_mode", "passthrough",
            "-pix_fmt", "bgr24", "-f", "rawvideo", "pipe:1"
        }) startInfo.ArgumentList.Add(argument);

        _process = Process.Start(startInfo) ?? throw new InvalidOperationException("Nie udało się uruchomić FFmpeg.");
        _readTask = ReadFramesAsync(_stop.Token);
        _errorTask = ReadErrorsAsync(_stop.Token);
    }

    public async ValueTask SubmitAsync(ReadOnlyMemory<byte> accessUnit)
    {
        Process? process = _process;
        if (process is null || process.HasExited || accessUnit.IsEmpty)
            return;

        await _writeLock.WaitAsync(_stop.Token);
        try
        {
            await process.StandardInput.BaseStream.WriteAsync(accessUnit, _stop.Token);
            await process.StandardInput.BaseStream.FlushAsync(_stop.Token);
        }
        catch (IOException ex)
        {
            LastError = $"FFmpeg zakończył dekodowanie: {ex.Message}";
        }
        catch (ObjectDisposedException) { }
        finally { _writeLock.Release(); }
    }

    private async Task ReadFramesAsync(CancellationToken cancellationToken)
    {
        if (_process is null) return;
        int frameLength = checked(width * height * 3);
        byte[] frame = new byte[frameLength];
        Stream output = _process.StandardOutput.BaseStream;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await output.ReadExactlyAsync(frame, cancellationToken);
                onFrame(new DecodedVideoFrame(frame, width, height));
                Interlocked.Increment(ref _decodedFrames);
                frame = new byte[frameLength];
            }
        }
        catch (OperationCanceledException) { }
        catch (EndOfStreamException) { }
        catch (IOException ex) when (!cancellationToken.IsCancellationRequested)
        {
            LastError = $"Błąd odczytu dekodera: {ex.Message}";
        }
    }

    private async Task ReadErrorsAsync(CancellationToken cancellationToken)
    {
        if (_process is null) return;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                string? line = await _process.StandardError.ReadLineAsync(cancellationToken);
                if (line is null) break;
                LastError = line;
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    internal static string FindFfmpeg()
    {
        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, "ffmpeg", "win-x64", "ffmpeg.exe"),
            Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe")
        ];
        string? bundled = candidates.FirstOrDefault(File.Exists);
        if (bundled is not null) return bundled;
        return "ffmpeg.exe"; // allow a local developer install on PATH
    }

    public async ValueTask DisposeAsync()
    {
        if (_process is null) return;
        _stop.Cancel();
        try { _process.StandardInput.Close(); } catch (InvalidOperationException) { }
        try { await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (TimeoutException) { _process.Kill(entireProcessTree: true); }
        if (_readTask is not null) await _readTask;
        if (_errorTask is not null) await _errorTask;
        _process.Dispose();
        _writeLock.Dispose();
        _stop.Dispose();
        _process = null;
    }
}
