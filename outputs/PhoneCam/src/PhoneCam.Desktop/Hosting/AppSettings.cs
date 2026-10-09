using System.IO;
using System.Text.Json;

namespace PhoneCam.Desktop.Hosting;

/// <summary>Small per-user preferences stored in %LOCALAPPDATA%\PhoneCam\settings.json.</summary>
internal sealed class AppSettings
{
    public string? PreferredInterfaceId { get; set; }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile)) ?? new();
        }
        catch (Exception ex) when (ex is IOException or JsonException) { }
        return new();
    }

    public void Save()
    {
        try { File.WriteAllText(AppPaths.SettingsFile, JsonSerializer.Serialize(this)); }
        catch (IOException) { }
    }
}
