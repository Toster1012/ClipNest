using System;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace ClipNest.Services;

public sealed class Preferences
{
    public const int MinimumCapacity = 1;
    public const int MaximumCapacity = 1000;
    public const int DefaultCapacity = 100;
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private static readonly string _settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipNest", "settings.json");

    public int Capacity { get; set; } = DefaultCapacity;

    public static Preferences Load()
    {
        try
        {
            Preferences preferences = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(_settingsPath)) ?? new Preferences();
            preferences.Capacity = Math.Clamp(preferences.Capacity, MinimumCapacity, MaximumCapacity);
            return preferences;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new Preferences();
        }
    }

    public static bool IsStartupEnabled()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue("ClipNest") is string;
    }

    public static void SetStartup(bool isEnabled)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey);

        if (isEnabled)
        {
            string executablePath = Environment.ProcessPath ?? throw new InvalidOperationException("Не найден путь приложения.");
            key.SetValue("ClipNest", $"\"{executablePath}\" --background");
        }
        else
        {
            key.DeleteValue("ClipNest", false);
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
        File.WriteAllText(_settingsPath, JsonSerializer.Serialize(this));
    }
}
