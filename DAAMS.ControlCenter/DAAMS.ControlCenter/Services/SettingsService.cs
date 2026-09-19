using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using ControlCenter.Models;

namespace ControlCenter.Services;

/// <summary>
/// Local persistence for Control Center preferences (Settings page).
///
/// Deliberately separate from AppConfig.cs / appsettings.json: that file
/// holds one piece of bundled, non-secret app configuration (the Firestore
/// project ID) shipped with the build. This service holds user-editable
/// preferences that change at runtime from the Settings UI, so it needs a
/// writable location — appsettings.json next to the exe may not even be
/// writable if the app is installed to Program Files. The standard place
/// for that in a Windows desktop app is the current user's AppData folder.
///
/// No Firestore, no cloud sync — purely local, per the Phase 6 boundary.
/// Every failure mode (missing file, corrupted JSON, permission error)
/// falls back to AppSettingsData.Defaults instead of throwing, so a bad
/// settings file can never crash the app or block startup.
/// </summary>
public class SettingsService
{
    private readonly string _filePath;

    public SettingsService()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DAAMS", "ControlCenter");

        _filePath = Path.Combine(dir, "settings.json");
    }

    public AppSettingsData Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return AppSettingsData.Defaults;
            }

            string json = File.ReadAllText(_filePath);
            AppSettingsData? data = JsonSerializer.Deserialize<AppSettingsData>(json);
            return data ?? AppSettingsData.Defaults;
        }
        catch (Exception ex)
        {
            // Missing file, corrupted JSON, permission error — any of these
            // falls back to safe defaults rather than crashing startup.
            Debug.WriteLine($"[SettingsService] Failed to load settings, using defaults: {ex}");
            return AppSettingsData.Defaults;
        }
    }

    /// <summary>Returns true on success. Never throws — a failed save is reported back as false so the ViewModel can show a friendly message instead of a crash.</summary>
    public bool Save(AppSettingsData data)
    {
        try
        {
            string? dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_filePath, json);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SettingsService] Failed to save settings: {ex}");
            return false;
        }
    }
}
