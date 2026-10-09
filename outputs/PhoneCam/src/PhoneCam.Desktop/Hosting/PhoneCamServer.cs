using System.Collections.Concurrent;
using System.IO;
using System.Net;

namespace PhoneCam.Desktop.Hosting;

public enum ServerState { Stopped, Starting, Running, Failed }

/// <summary>Owns the local server pair (Kestrel page + MediaMTX) for the currently selected LAN address.</summary>
internal sealed class PhoneCamServer : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private PhoneWebServer? _web;
    private MediaMtxProcess? _mediaMtx;
    private readonly ConcurrentDictionary<string, int> _rotations = new();

    public ServerState State { get; private set; }

    /// <summary>Raised on any thread with the slot whose reported rotation changed.</summary>
    public event Action<string>? OrientationChanged;

    /// <summary>Last rotation (degrees counter-clockwise) the phone asked for; 0 until it reports.</summary>
    public int GetRotation(string slot) => _rotations.GetValueOrDefault(slot);

    private void OnOrientation(string slot, int rotation)
    {
        if (_rotations.TryGetValue(slot, out int previous) && previous == rotation) return;
        _rotations[slot] = rotation;
        OrientationChanged?.Invoke(slot);
    }
    public string? Error { get; private set; }
    public IPAddress? Address { get; private set; }

    /// <summary>Raised on any thread; UI must marshal.</summary>
    public event Action? StateChanged;

    public string CertificateUrl => $"http://{Address}:{Ports.CertificateDownload}/PhoneCam-RootCA.cer";
    public string PublishUrl(string slot) => $"https://{Address}:{Ports.PhonePage}/{slot}";
    public static string RtspUrl(string slot) => $"rtsp://127.0.0.1:{Ports.Rtsp}/{slot}";

    public async Task StartAsync(IPAddress address)
    {
        await _gate.WaitAsync();
        try
        {
            await StopCoreAsync();
            Address = address;
            SetState(ServerState.Starting, null);

            ServerCertificate certificate = await Task.Run(() => LocalCertificates.EnsureServerCertificate(address));

            _web = new PhoneWebServer();
            try { await _web.StartAsync(certificate.Certificate, OnOrientation); }
            catch (IOException ex)
            {
                throw new InvalidOperationException(
                    $"Porty {Ports.PhonePage}/{Ports.CertificateDownload} są zajęte. Czy działa inna kopia PhoneCam albo skrypt testowy? ({ex.Message})", ex);
            }

            _mediaMtx = new MediaMtxProcess();
            _mediaMtx.Exited += lastError => SetState(ServerState.Failed,
                $"MediaMTX zakończył działanie. {lastError ?? ""} Log: {MediaMtxProcess.LogPath}");
            _mediaMtx.Start(address, certificate);

            // MediaMTX fails fast (config error, busy port); give it a moment before declaring success.
            await Task.Delay(700);
            if (!_mediaMtx.IsRunning)
                throw new InvalidOperationException($"MediaMTX nie wystartował. {_mediaMtx.LastError} Log: {MediaMtxProcess.LogPath}");

            SetState(ServerState.Running, null);
        }
        catch (Exception ex)
        {
            await StopCoreAsync();
            SetState(ServerState.Failed, ex.Message);
        }
        finally { _gate.Release(); }
    }

    private void SetState(ServerState state, string? error)
    {
        State = state;
        Error = error;
        StateChanged?.Invoke();
    }

    private async Task StopCoreAsync()
    {
        if (_mediaMtx is not null) await _mediaMtx.DisposeAsync();
        if (_web is not null) await _web.DisposeAsync();
        _mediaMtx = null;
        _web = null;
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await StopCoreAsync();
            State = ServerState.Stopped;
        }
        finally { _gate.Release(); }
    }
}
