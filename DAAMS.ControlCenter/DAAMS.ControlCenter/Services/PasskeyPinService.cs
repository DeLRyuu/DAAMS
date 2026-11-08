using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Google.Cloud.Firestore;

namespace ControlCenter.Services;

public enum CloudPinStatus
{
    /// <summary>The Passkey PIN document exists in Firestore.</summary>
    Found,
    /// <summary>Firestore was reached but no Passkey PIN has been generated yet.</summary>
    Missing,
    /// <summary>Offline, no key, or any error. Does NOT mean "no PIN exists".</summary>
    Unreachable
}

public sealed record CloudPinResult(
    CloudPinStatus Status,
    string PinHash = "",
    string Salt = "",
    string UpdatedAt = "",
    string UpdatedBy = "");

/// <summary>
/// "Passkey PIN": a random 4-digit PIN generated here in the Control Center and saved to Firestore
/// (security_settings/pin_config) as a salted SHA-256 hash, the same format pin_auth.py uses:
/// SHA256(salt + pin), lowercase hex. The Control Center is the ONLY writer. Monitoring Engines
/// download it and keep a local copy, so PIN checks keep working offline.
/// The PIN itself is never stored or sent -- only the hash and salt.
/// </summary>
public static class PasskeyPinService
{
    private const string CollectionName = "security_settings";
    private const string DocumentId = "pin_config";

    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(20);
    private static readonly object CacheLock = new();
    private static CloudPinResult? _cache;
    private static DateTime _cacheTimeUtc;

    // ------------------------------------------------------------------
    // Local, encrypted copy of the PIN so THIS PC's admin can look at it (eye button in Settings).
    // Protected with Windows DPAPI for the current Windows user: only this account on this PC can
    // decrypt it. It is never sent to Firestore -- Firestore only ever holds the hash.
    // ------------------------------------------------------------------
    private static string LocalPinPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DAAMS", "ControlCenter", "passkey.dat");

    public static void SaveLocalPin(string pin)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LocalPinPath)!);
            byte[] protectedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(pin), null, DataProtectionScope.CurrentUser);
            File.WriteAllText(LocalPinPath, Convert.ToBase64String(protectedBytes));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PasskeyPin] Could not store the PIN locally: {ex.Message}");
        }
    }

    /// <summary>The PIN this PC generated, or null if there is none or it cannot be decrypted. Never throws.</summary>
    public static string? LoadLocalPin()
    {
        try
        {
            if (!File.Exists(LocalPinPath))
            {
                return null;
            }

            byte[] plain = ProtectedData.Unprotect(
                Convert.FromBase64String(File.ReadAllText(LocalPinPath)), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PasskeyPin] Could not read the locally stored PIN: {ex.Message}");
            return null;
        }
    }

    public static void DeleteLocalPin()
    {
        try
        {
            if (File.Exists(LocalPinPath))
            {
                File.Delete(LocalPinPath);
            }
        }
        catch
        {
            // Not worth surfacing.
        }
    }

    /// <summary>Random 4-digit PIN, skipping obvious ones (0000, 1111, ... and the old default 1234).</summary>
    public static string GeneratePin()
    {
        while (true)
        {
            string pin = RandomNumberGenerator.GetInt32(0, 10000).ToString("D4");
            if (pin == "1234" || pin.Distinct().Count() == 1)
            {
                continue;
            }

            return pin;
        }
    }

    public static (string PinHash, string Salt) HashPin(string pin)
    {
        string salt = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        return (ComputeHash(salt, pin), salt);
    }

    public static bool Matches(string candidatePin, string pinHash, string salt)
    {
        if (string.IsNullOrEmpty(candidatePin) || string.IsNullOrEmpty(pinHash))
        {
            return false;
        }

        // Fixed-time comparison of the two lowercase-hex hashes.
        byte[] computed = Encoding.UTF8.GetBytes(ComputeHash(salt, candidatePin));
        byte[] stored = Encoding.UTF8.GetBytes(pinHash.Trim().ToLowerInvariant());
        return CryptographicOperations.FixedTimeEquals(computed, stored);
    }

    private static string ComputeHash(string salt, string pin)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(salt + pin));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>Saves a new Passkey PIN to Firestore (replacing the previous one) and returns its hash and salt.</summary>
    public static async Task<(string PinHash, string Salt)> PublishAsync(string pin, string updatedBy, CancellationToken ct = default)
    {
        var (hash, salt) = HashPin(pin);
        FirestoreDb db = await FirestoreConnectionService.Shared.GetDbAsync();

        string updatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        var data = new Dictionary<string, object>
        {
            ["pin_hash"] = hash,
            ["salt"] = salt,
            ["updated_at"] = updatedAt,
            ["updated_by"] = updatedBy,
            ["is_default"] = false,
            ["managed_by"] = "control-center"
        };

        // No merge: the whole document is replaced, so nothing from an older PIN can linger.
        await db.Collection(CollectionName).Document(DocumentId).SetAsync(data, cancellationToken: ct);

        lock (CacheLock)
        {
            _cache = new CloudPinResult(CloudPinStatus.Found, hash, salt, updatedAt, updatedBy);
            _cacheTimeUtc = DateTime.UtcNow;
        }

        return (hash, salt);
    }

    /// <summary>Reads the Passkey PIN document from Firestore. Never throws.</summary>
    public static async Task<CloudPinResult> FetchAsync(CancellationToken ct = default)
    {
        CloudPinResult result;
        try
        {
            FirestoreDb db = await FirestoreConnectionService.Shared.GetDbAsync();
            DocumentSnapshot snapshot = await db.Collection(CollectionName).Document(DocumentId).GetSnapshotAsync(ct);

            if (snapshot.Exists
                && snapshot.TryGetValue("pin_hash", out string? hash)
                && snapshot.TryGetValue("salt", out string? salt)
                && !string.IsNullOrWhiteSpace(hash)
                && !string.IsNullOrWhiteSpace(salt))
            {
                snapshot.TryGetValue("updated_at", out string? updatedAt);
                snapshot.TryGetValue("updated_by", out string? updatedBy);
                result = new CloudPinResult(CloudPinStatus.Found, hash.Trim(), salt, updatedAt ?? "", updatedBy ?? "");
            }
            else
            {
                result = new CloudPinResult(CloudPinStatus.Missing);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PasskeyPin] Could not read the Passkey PIN from Firestore: {ex.Message}");
            return new CloudPinResult(CloudPinStatus.Unreachable);
        }

        lock (CacheLock)
        {
            _cache = result;
            _cacheTimeUtc = DateTime.UtcNow;
        }

        return result;
    }

    /// <summary>
    /// Synchronous lookup for callers that cannot await (PIN verification). Cached for a few seconds,
    /// and gives up after <paramref name="timeout"/> so an offline PC never hangs the dialog.
    /// </summary>
    public static CloudPinResult FetchBlocking(TimeSpan timeout)
    {
        lock (CacheLock)
        {
            if (_cache is not null && DateTime.UtcNow - _cacheTimeUtc < CacheLifetime)
            {
                return _cache;
            }
        }

        try
        {
            using var cts = new CancellationTokenSource(timeout);
            Task<CloudPinResult> task = Task.Run(() => FetchAsync(cts.Token));
            return task.Wait(timeout) ? task.Result : new CloudPinResult(CloudPinStatus.Unreachable);
        }
        catch (Exception)
        {
            return new CloudPinResult(CloudPinStatus.Unreachable);
        }
    }
}
