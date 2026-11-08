using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using ControlCenter.Models;
using Microsoft.Data.Sqlite;

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

    /// <summary>
    /// Loads notification_email from data/admin_settings.json, falling back to DAAMS_NOTIFICATION_EMAIL, then empty string.
    /// </summary>
    public static string LoadNotificationEmail(string? dbPath = null)
    {
        string defaultEmail = Environment.GetEnvironmentVariable("DAAMS_NOTIFICATION_EMAIL")?.Trim() ?? string.Empty;
        string path = ResolveAdminSettingsPath(dbPath);

        try
        {
            if (File.Exists(path))
            {
                string json = File.ReadAllText(path);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("notification_email", out var prop))
                {
                    string? fileVal = prop.GetString();
                    if (fileVal != null)
                    {
                        return fileVal.Trim();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SettingsService] Failed to read notification_email from admin_settings.json: {ex}");
        }

        return defaultEmail;
    }

    /// <summary>
    /// Validates an email address per Phase 9e: one address, '@' and a dot after it, max 254 chars.
    /// </summary>
    public static bool IsValidEmail(string email)
    {
        if (string.IsNullOrWhiteSpace(email)) return false;
        string trimmed = email.Trim();
        if (trimmed.Length > 254) return false;
        if (trimmed.Contains(' ') || trimmed.Contains('\t') || trimmed.Contains(',') || trimmed.Contains(';')) return false;

        int atIndex = trimmed.IndexOf('@');
        if (atIndex <= 0 || atIndex != trimmed.LastIndexOf('@')) return false;

        string domain = trimmed.Substring(atIndex + 1);
        if (string.IsNullOrEmpty(domain)) return false;
        int dotIndex = domain.IndexOf('.');
        if (dotIndex <= 0 || domain.EndsWith('.')) return false;

        return true;
    }

    /// <summary>
    /// Atomically updates notification_email in data/admin_settings.json, preserving any other keys.
    /// Never stores passwords; no password fields.
    /// </summary>
    public static bool SaveNotificationEmail(string email, string? dbPath = null)
    {
        if (!IsValidEmail(email)) return false;

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

            jsonObj["notification_email"] = email.Trim();

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
            Debug.WriteLine($"[SettingsService] Failed to save notification_email: {ex}");
            return false;
        }
    }

    /// <summary>
    /// Resolves the canonical path to the smtp_settings.json file in the database directory.
    /// </summary>
    public static string ResolveSmtpSettingsPath(string? dbPath = null)
    {
        string? envSmtpSettings = Environment.GetEnvironmentVariable("DAAMS_SMTP_SETTINGS_PATH");
        if (!string.IsNullOrWhiteSpace(envSmtpSettings))
        {
            return Path.GetFullPath(envSmtpSettings.Trim());
        }

        string effectiveDbPath = !string.IsNullOrWhiteSpace(dbPath)
            ? dbPath
            : LocalDatabaseService.ResolveDatabasePath();

        string? dir = Path.GetDirectoryName(Path.GetFullPath(effectiveDbPath));
        if (string.IsNullOrEmpty(dir))
        {
            dir = AppContext.BaseDirectory;
        }

        return Path.Combine(dir, "smtp_settings.json");
    }

    /// <summary>
    /// Encrypts password using DPAPI (CurrentUser scope). Never logs or prints plaintext password.
    /// </summary>
    public static string EncryptPassword(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return string.Empty;
        try
        {
            byte[] plainBytes = System.Text.Encoding.UTF8.GetBytes(plaintext);
            byte[] protectedBytes = System.Security.Cryptography.ProtectedData.Protect(
                plainBytes,
                null,
                System.Security.Cryptography.DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(protectedBytes);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SettingsService] Encryption failed: {ex.Message}");
            return string.Empty;
        }
    }

    /// <summary>
    /// Decrypts DPAPI-protected password. Never logs or prints plaintext password.
    /// </summary>
    public static string DecryptPassword(string base64Protected)
    {
        if (string.IsNullOrEmpty(base64Protected)) return string.Empty;
        try
        {
            byte[] cipherBytes = Convert.FromBase64String(base64Protected);
            byte[] plainBytes = System.Security.Cryptography.ProtectedData.Unprotect(
                cipherBytes,
                null,
                System.Security.Cryptography.DataProtectionScope.CurrentUser);
            return System.Text.Encoding.UTF8.GetString(plainBytes);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SettingsService] Decryption failed: {ex.Message}");
            return string.Empty;
        }
    }

    /// <summary>
    /// Loads SMTP configuration from data/smtp_settings.json.
    /// Falls back to DAAMS_SMTP_USERNAME and DAAMS_EMAIL_ENABLED if missing or invalid.
    /// </summary>
    public static (string SenderEmail, string PasswordProtected, bool Enabled) LoadSmtpSettings(string? dbPath = null)
    {
        string path = ResolveSmtpSettingsPath(dbPath);
        try
        {
            if (File.Exists(path))
            {
                string json = File.ReadAllText(path);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    string senderEmail = doc.RootElement.TryGetProperty("sender_email", out var sProp) && sProp.GetString() != null
                        ? sProp.GetString()!.Trim()
                        : string.Empty;

                    string passwordProtected = doc.RootElement.TryGetProperty("password_protected", out var pProp) && pProp.GetString() != null
                        ? pProp.GetString()!.Trim()
                        : string.Empty;

                    bool enabled = doc.RootElement.TryGetProperty("enabled", out var eProp) && eProp.ValueKind switch
                    {
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        _ => false
                    };

                    return (senderEmail, passwordProtected, enabled);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SettingsService] Failed to read smtp_settings.json: {ex.Message}");
        }

        string envSender = Environment.GetEnvironmentVariable("DAAMS_SMTP_USERNAME")?.Trim() ?? string.Empty;
        bool envEnabled = string.Equals(Environment.GetEnvironmentVariable("DAAMS_EMAIL_ENABLED")?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
        return (envSender, string.Empty, envEnabled);
    }

    /// <summary>
    /// Atomically writes SMTP settings to data/smtp_settings.json.
    /// Never writes plaintext passwords to disk or logs.
    /// </summary>
    public static bool SaveSmtpSettings(string senderEmail, string passwordProtected, bool enabled, string? dbPath = null)
    {
        try
        {
            string path = ResolveSmtpSettingsPath(dbPath);
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var jsonObj = new JsonObject
            {
                ["sender_email"] = senderEmail.Trim(),
                ["password_protected"] = passwordProtected ?? string.Empty,
                ["enabled"] = enabled
            };

            string tempFile = Path.Combine(dir ?? ".", $"smtp_settings_{Guid.NewGuid():N}.tmp");
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
            Debug.WriteLine($"[SettingsService] Failed to save smtp_settings.json: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Resolves the canonical path to the security_settings.json file in the database directory.
    /// </summary>
    public static string ResolveSecuritySettingsPath(string? dbPath = null)
    {
        string? envPath = Environment.GetEnvironmentVariable("DAAMS_SECURITY_SETTINGS_PATH");
        if (!string.IsNullOrWhiteSpace(envPath))
        {
            return Path.GetFullPath(envPath.Trim());
        }

        string effectiveDbPath = !string.IsNullOrWhiteSpace(dbPath)
            ? dbPath
            : LocalDatabaseService.ResolveDatabasePath();

        string? dir = Path.GetDirectoryName(Path.GetFullPath(effectiveDbPath));
        if (string.IsNullOrEmpty(dir))
        {
            dir = AppContext.BaseDirectory;
        }

        return Path.Combine(dir, "security_settings.json");
    }

    /// <summary>
    /// Loads the stored PIN hash and salt from SQLite or security_settings.json.
    /// Never logs or exposes the hash or salt.
    /// </summary>
    public static (string PinHash, string Salt)? LoadSecuritySettings(string? dbPath = null)
    {
        // 1. Try SQLite
        try
        {
            string effectiveDb = !string.IsNullOrWhiteSpace(dbPath) ? dbPath : LocalDatabaseService.ResolveDatabasePath();
            if (File.Exists(effectiveDb))
            {
                using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource = effectiveDb,
                    Mode = SqliteOpenMode.ReadOnly,
                    DefaultTimeout = 5
                }.ToString());
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT pin_hash, salt FROM security_settings WHERE key = 'pin_config' AND COALESCE(is_default, 0) = 0 LIMIT 1;";
                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                {
                    string h = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                    string s = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                    if (!string.IsNullOrWhiteSpace(h))
                    {
                        return (h.Trim(), s);
                    }
                }
            }
        }
        catch { }

        // 2. Try JSON file fallback
        try
        {
            string jsonPath = ResolveSecuritySettingsPath(dbPath);
            if (File.Exists(jsonPath))
            {
                string json = File.ReadAllText(jsonPath);
                using var doc = JsonDocument.Parse(json);
                bool isDefault = doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("is_default", out var dProp)
                    && dProp.ValueKind == JsonValueKind.True;
                if (doc.RootElement.ValueKind == JsonValueKind.Object && !isDefault)
                {
                    string h = doc.RootElement.TryGetProperty("pin_hash", out var hProp) ? hProp.GetString() ?? "" : "";
                    string s = doc.RootElement.TryGetProperty("salt", out var sProp) ? sProp.GetString() ?? "" : "";
                    if (!string.IsNullOrEmpty(h))
                    {
                        return (h, s);
                    }
                }
            }
        }
        catch { }

        return null;
    }

    /// <summary>
    /// Verifies a PIN against the Passkey PIN: SHA256(salt + pin), same as pin_auth.py.
    /// Online, Firestore is the authority (a regenerated Passkey works immediately);
    /// offline, the local copy is used. There is NO default PIN any more.
    /// Never logs or prints the candidate PIN, hash, or salt.
    /// Blocks for up to a few seconds when offline, so call it off the UI thread.
    /// </summary>
    public static bool VerifyPin(string candidatePin, string? dbPath = null)
    {
        if (string.IsNullOrEmpty(candidatePin)) return false;

        var stored = LoadSecuritySettings(dbPath);
        bool online = FirestoreConnectionService.Shared.State == FirestoreConnectionState.Connected;

        if (online || !stored.HasValue)
        {
            CloudPinResult cloud = PasskeyPinService.FetchBlocking(TimeSpan.FromSeconds(3));
            if (cloud.Status == CloudPinStatus.Found)
            {
                return PasskeyPinService.Matches(candidatePin, cloud.PinHash, cloud.Salt);
            }
        }

        return stored.HasValue && PasskeyPinService.Matches(candidatePin, stored.Value.PinHash, stored.Value.Salt);
    }

    /// <summary>
    /// True if a Passkey PIN exists that this PC can check against: a local copy, or one in Firestore.
    /// </summary>
    public static bool HasAnyPasskey(string? dbPath = null)
    {
        if (LoadSecuritySettings(dbPath).HasValue) return true;
        return PasskeyPinService.FetchBlocking(TimeSpan.FromSeconds(2)).Status == CloudPinStatus.Found;
    }

    /// <summary>
    /// Saves the Passkey PIN locally (SQLite + security_settings.json) on a PC that has a local DAAMS database.
    /// Marked SYNCED: the PIN is managed in Firestore and the Engine never uploads it.
    /// Does nothing on a Control Center-only PC (no local database); that PC reads the PIN from Firestore.
    /// </summary>
    public static void StoreLocalPin(string pinHash, string salt, string updatedBy, string? dbPath = null)
    {
        try
        {
            string effectiveDb = !string.IsNullOrWhiteSpace(dbPath) ? dbPath : LocalDatabaseService.ResolveDatabasePath();
            if (!File.Exists(effectiveDb))
            {
                return;
            }

            string nowStr = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

            string jsonPath = ResolveSecuritySettingsPath(effectiveDb);
            string? dir = Path.GetDirectoryName(jsonPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var jsonObj = new JsonObject
            {
                ["pin_hash"] = pinHash,
                ["salt"] = salt,
                ["updated_at"] = nowStr,
                ["updated_by"] = updatedBy,
                ["is_default"] = false
            };

            string tempFile = Path.Combine(dir ?? ".", $"security_settings_{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllText(tempFile, jsonObj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                File.Move(tempFile, jsonPath, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
            }

            using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = effectiveDb,
                Mode = SqliteOpenMode.ReadWrite,
                DefaultTimeout = 5
            }.ToString());
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO security_settings (key, pin_hash, salt, updated_at, updated_by, is_default, sync_status, synced_at, sync_error)
                VALUES ('pin_config', @hash, @salt, @updated_at, @updated_by, 0, 'SYNCED', @synced_at, NULL)
                ON CONFLICT(key) DO UPDATE SET
                    pin_hash = excluded.pin_hash,
                    salt = excluded.salt,
                    updated_at = excluded.updated_at,
                    updated_by = excluded.updated_by,
                    is_default = 0,
                    sync_status = 'SYNCED',
                    synced_at = excluded.synced_at,
                    sync_error = NULL;";
            cmd.Parameters.AddWithValue("@hash", pinHash);
            cmd.Parameters.AddWithValue("@salt", salt);
            cmd.Parameters.AddWithValue("@updated_at", nowStr);
            cmd.Parameters.AddWithValue("@updated_by", updatedBy);
            cmd.Parameters.AddWithValue("@synced_at", nowStr);
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SettingsService] StoreLocalPin warning: {ex.Message}");
        }
    }
}
