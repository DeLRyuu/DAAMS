using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
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

    /// <summary>
    /// Resolves the canonical path to the shared admin_settings.json file in the database directory.
    /// </summary>
    public static string ResolveAdminSettingsPath(string? dbPath = null)
    {
        string? envAdminSettings = Environment.GetEnvironmentVariable("DAAMS_ADMIN_SETTINGS_PATH");
        if (!string.IsNullOrWhiteSpace(envAdminSettings))
        {
            return Path.GetFullPath(envAdminSettings.Trim());
        }

        string effectiveDbPath = !string.IsNullOrWhiteSpace(dbPath)
            ? dbPath
            : LocalDatabaseService.ResolveDatabasePath();

        string? dir = Path.GetDirectoryName(Path.GetFullPath(effectiveDbPath));
        if (string.IsNullOrEmpty(dir))
        {
            dir = AppContext.BaseDirectory;
        }

        return Path.Combine(dir, "admin_settings.json");
    }

    /// <summary>
    /// Loads work hours from data/admin_settings.json, falling back to DAAMS_WORK_START/END, then 7 and 19.
    /// </summary>
    public static (int Start, int End) LoadWorkHours(string? dbPath = null)
    {
        int defaultStart = 7;
        int defaultEnd = 19;

        string? envStart = Environment.GetEnvironmentVariable("DAAMS_WORK_START");
        if (int.TryParse(envStart, out int parsedEnvStart) && parsedEnvStart >= 0 && parsedEnvStart <= 24)
        {
            defaultStart = parsedEnvStart;
        }

        string? envEnd = Environment.GetEnvironmentVariable("DAAMS_WORK_END");
        if (int.TryParse(envEnd, out int parsedEnvEnd) && parsedEnvEnd >= 0 && parsedEnvEnd <= 24)
        {
            defaultEnd = parsedEnvEnd;
        }

        string path = ResolveAdminSettingsPath(dbPath);
        int? fileStart = null;
        int? fileEnd = null;

        try
        {
            if (File.Exists(path))
            {
                string json = File.ReadAllText(path);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    if (doc.RootElement.TryGetProperty("work_start", out var startProp) &&
                        startProp.TryGetInt32(out int s) && s >= 0 && s <= 24)
                    {
                        fileStart = s;
                    }

                    if (doc.RootElement.TryGetProperty("work_end", out var endProp) &&
                        endProp.TryGetInt32(out int e) && e >= 0 && e <= 24)
                    {
                        fileEnd = e;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SettingsService] Failed to read admin_settings.json, falling back: {ex}");
        }

        int finalStart = fileStart ?? defaultStart;
        int finalEnd = fileEnd ?? defaultEnd;

        if (finalStart == finalEnd)
        {
            finalStart = defaultStart;
            finalEnd = defaultEnd;
            if (finalStart == finalEnd)
            {
                finalStart = 7;
                finalEnd = 19;
            }
        }

        return (finalStart, finalEnd);
    }

    /// <summary>
    /// Atomically updates work_start and work_end in data/admin_settings.json, preserving any other keys.
    /// </summary>
    public static bool SaveWorkHours(int start, int end, string? dbPath = null)
    {
        if (start < 0 || start > 24 || end < 0 || end > 24 || start == end)
        {
            return false;
        }

        try
        {
            string path = ResolveAdminSettingsPath(dbPath);
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            JsonObject jsonObj;
            if (File.Exists(path))
            {
                try
                {
                    string existingJson = File.ReadAllText(path);
                    jsonObj = JsonNode.Parse(existingJson) as JsonObject ?? new JsonObject();
                }
                catch
                {
                    jsonObj = new JsonObject();
                }
            }
            else
            {
                jsonObj = new JsonObject();
            }

            // Change only the keys being saved:
            jsonObj["work_start"] = start;
            jsonObj["work_end"] = end;

            string tempFile = Path.Combine(dir ?? ".", $"admin_settings_{Guid.NewGuid():N}.tmp");
            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                File.WriteAllText(tempFile, jsonObj.ToJsonString(options));
                File.Move(tempFile, path, overwrite: true);
                return true;
            }
            finally
            {
                try
                {
                    if (File.Exists(tempFile))
                    {
                        File.Delete(tempFile);
                    }
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SettingsService] Failed to save admin_settings.json: {ex}");
            return false;
        }
    }
}
