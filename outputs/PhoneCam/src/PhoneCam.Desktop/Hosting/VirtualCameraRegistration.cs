using System.IO;
using Microsoft.Win32;
using PhoneCam.Desktop.Video;

namespace PhoneCam.Desktop.Hosting;

/// <summary>
/// The DirectShow filter must be registered machine-wide (regsvr32, administrator) so OBS, Teams and others
/// list "PhoneCam iPhone". Registration points at the DLL next to the exe; moving the app re-registers.
/// </summary>
internal static class VirtualCameraRegistration
{
    // Must match CLSID_PhoneCamIphone in PhoneCam.VirtualCamera.cpp.
    private const string IphoneClsid = "{1C15ECD3-BE4A-4643-BC8E-D570C72CD3C8}";
    private const string VideoInputDeviceCategory = "{860BB310-5D01-11D0-BD3B-00A0C911CE86}";

    public static string DllPath => Path.Combine(AppContext.BaseDirectory, VirtualCamera.DllName);

    /// <summary>False when the native DLL was not built (Visual Studio C++ tools missing).</summary>
    public static bool DllPresent => File.Exists(DllPath);

    public static bool IsRegistered()
    {
        using RegistryKey? server = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Classes\CLSID\{IphoneClsid}\InprocServer32");
        using RegistryKey? device = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Classes\CLSID\{VideoInputDeviceCategory}\Instance\{IphoneClsid}");
        return device is not null
            && server?.GetValue(null) is string path
            && string.Equals(Path.GetFullPath(path), Path.GetFullPath(DllPath), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>cmd lines for the elevated setup script.</summary>
    public static string SetupCommands() => $"regsvr32 /s \"{DllPath}\"\r\n";
}
