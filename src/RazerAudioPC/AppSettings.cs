using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace RazerAudioPC;

/// <summary>App preferences, stored in %AppData%\RazerAudioPC\settings.json.</summary>
public sealed class AppSettings
{
    public static readonly string Folder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RazerAudioPC");

    private static readonly string FilePath = Path.Combine(Folder, "settings.json");
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "RazerAudioPC";

    public bool CloseToTray { get; set; } = true;
    public bool BatteryNotifications { get; set; } = true;

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath)) return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch { /* fall back to defaults */ }
        return new();
    }

    public void Save()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static bool StartWithWindows
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunValue) != null;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) key.SetValue(RunValue, $"\"{Environment.ProcessPath}\" --minimized");
            else key.DeleteValue(RunValue, false);
        }
    }
}
