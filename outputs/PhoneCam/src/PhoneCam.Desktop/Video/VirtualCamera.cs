using System.Runtime.InteropServices;

namespace PhoneCam.Desktop.Video;

/// <summary>
/// Sender side of one PhoneCam virtual camera (DirectShow device such as "PhoneCam iPhone"). Frames sent
/// here reach every app that opened the camera (OBS, Teams, Zoom, browsers) through shared memory in
/// PhoneCam.VirtualCamera.dll; the same DLL is the filter those apps load.
/// </summary>
internal sealed class VirtualCamera : IDisposable
{
    public const int Width = 1280;
    public const int Height = 720;
    public const string DllName = "PhoneCam.VirtualCamera.dll";

    private readonly Lock _lock = new();
    private IntPtr _handle;

    private VirtualCamera(IntPtr handle) => _handle = handle;

    /// <summary>
    /// Index into the DLL's camera table (0 = iPhone). Returns null when the DLL is missing or the camera's
    /// frame buffer still exists, e.g. another sender runs or an app holds the previous session open.
    /// </summary>
    public static VirtualCamera? TryCreate(int camera)
    {
        try
        {
            IntPtr handle = pcCreateCamera(camera, Width, Height);
            return handle == IntPtr.Zero ? null : new VirtualCamera(handle);
        }
        catch (DllNotFoundException) { return null; }
    }

    /// <summary>True while at least one app reads from the camera.</summary>
    public bool InUse
    {
        get { lock (_lock) return _handle != IntPtr.Zero && pcIsConnected(_handle); }
    }

    /// <summary>Publishes a top-down BGR24 frame of exactly <see cref="Width"/>×<see cref="Height"/>.</summary>
    public void Send(byte[] bgr24)
    {
        if (bgr24.Length != Width * Height * 3) throw new ArgumentException("Nieprawidłowy rozmiar klatki.", nameof(bgr24));
        lock (_lock)
        {
            if (_handle != IntPtr.Zero) pcSendFrame(_handle, bgr24);
        }
    }

    /// <summary>Apps keep showing the last frame, darkened, until PhoneCam sends again.</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_handle == IntPtr.Zero) return;
            pcDeleteCamera(_handle);
            _handle = IntPtr.Zero;
        }
    }

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr pcCreateCamera(int camera, int width, int height);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void pcDeleteCamera(IntPtr camera);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void pcSendFrame(IntPtr camera, byte[] imageBits);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool pcIsConnected(IntPtr camera);
}
