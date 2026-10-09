using System.IO;

namespace PhoneCam.Desktop.Hosting;

/// <summary>Per-user state lives in %LOCALAPPDATA%\PhoneCam; bundled binaries live next to the exe.</summary>
internal static class AppPaths
{
    /// <summary>Overridable so checks can run against a throwaway directory.</summary>
    public static string Root { get; set; } = Directory.CreateDirectory(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhoneCam")).FullName;

    public static string Certificates => Ensure("cert");
    public static string MediaMtxRuntime => Ensure("mediamtx");
    public static string Logs => Ensure("logs");
    public static string SettingsFile => Path.Combine(Root, "settings.json");

    public static string MediaMtxExe { get; set; } = Path.Combine(AppContext.BaseDirectory, "mediamtx", "mediamtx.exe");
    public static string PhonePage => Path.Combine(AppContext.BaseDirectory, "Web", "phonecam.html");
    public static string AppExe => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "PhoneCam.Desktop.exe");

    private static string Ensure(string name) => Directory.CreateDirectory(Path.Combine(Root, name)).FullName;
}

internal static class Ports
{
    public const int PhonePage = 8443;           // HTTPS, LAN
    public const int CertificateDownload = 8787; // HTTP, LAN
    public const int Whip = 8889;                // MediaMTX WebRTC/WHIP over HTTPS, LAN
    public const int WebRtcUdp = 8189;           // MediaMTX WebRTC media, LAN
    public const int Rtsp = 8554;                // loopback only
    public const int MediaMtxApi = 9997;         // loopback only
}

internal static class Slots
{
    public const string Iphone = "iphone";
    public const string Android = "android";
    public static bool IsKnown(string slot) => slot is Iphone or Android;
}
