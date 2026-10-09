using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace PhoneCam.Desktop.Hosting;

/// <summary>Runs a short cmd script as administrator: one UAC prompt, no console window.</summary>
internal static class Elevated
{
    /// <summary>Returns false if the user declined UAC.</summary>
    public static async Task<bool> RunScriptAsync(string fileName, string commands)
    {
        string script = Path.Combine(AppPaths.Root, fileName);
        await File.WriteAllTextAsync(script, "@echo off\r\n" + commands, Encoding.ASCII);
        try
        {
            using Process process = Process.Start(new ProcessStartInfo(script)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            })!;
            await process.WaitForExitAsync();
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED: UAC declined
        {
            return false;
        }
        finally { File.Delete(script); }
    }
}
