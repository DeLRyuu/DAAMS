using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.Firestore;
using Google.Cloud.Firestore.V1;

namespace ControlCenter.Services;

/// <summary>
/// Connection state for the app's single shared Firestore link.
/// </summary>
public enum FirestoreConnectionState
{
    NotConfigured,
    Connecting,
    Connected,
    Unavailable
}

/// <summary>
/// Owns the single FirestoreDb instance for the app and tracks connection
/// status so the UI (shell footer, per-section error states) can reflect it.
///
/// Credential lookup order (first match wins):
///   1. GOOGLE_APPLICATION_CREDENTIALS environment variable
///   2. %LOCALAPPDATA%\DAAMS\serviceAccountKey.json
///   3. serviceAccountKey.json next to the exe (installed build)
///   4. monitoring_engine\serviceAccountKey.json found by walking up from the exe (dev repo)
///
/// Connection errors are written to %LOCALAPPDATA%\DAAMS\controlcenter.log.
/// </summary>
public class FirestoreConnectionService
{
    private const string KeyNotFoundStatus =
        "Cloud: key not found (put serviceAccountKey.json next to the app or in %LOCALAPPDATA%\\DAAMS)";
    private static FirestoreConnectionService? _shared;

    /// <summary>
    /// The app's one shared connection. The first instance ever created becomes the shared one,
    /// so the instance the app already creates at startup IS Shared (no second Firestore link).
    /// Static helpers such as PasskeyPinService and SettingsService.VerifyPin use this.
    /// </summary>
    public static FirestoreConnectionService Shared
    {
        get
        {
            if (_shared is null)
            {
                _ = new FirestoreConnectionService(); // the constructor registers itself as Shared
            }

            return _shared!;
        }
    }

    public FirestoreConnectionService()
    {
        Interlocked.CompareExchange(ref _shared, this, null);
    }

    private readonly object _connectLock = new();
    private FirestoreDb? _db;
    private Task<FirestoreDb>? _connectTask;

    public FirestoreConnectionState State { get; private set; } = FirestoreConnectionState.NotConfigured;
    public string StatusMessage { get; private set; } = KeyNotFoundStatus;
    public string? ProjectId { get; private set; }

    /// <summary>Raised whenever State/StatusMessage change, so the shell footer can stay live.</summary>
    public event EventHandler? StateChanged;

    /// <summary>
    /// Returns the connected FirestoreDb, connecting on first call. Concurrent
    /// callers share the same in-flight connection attempt instead of racing
    /// to connect independently. Throws if the project isn't configured or
    /// the connection fails — callers (FirestoreService) are expected to
    /// catch this and surface a friendly per-section error state rather than
    /// letting it bubble up as a crash.
    /// </summary>
    public Task<FirestoreDb> GetDbAsync()
    {
        lock (_connectLock)
        {
            if (_db is not null)
            {
                return Task.FromResult(_db);
            }

            _connectTask ??= ConnectAsync();
            return _connectTask;
        }
    }

    private async Task<FirestoreDb> ConnectAsync()
    {
        await Task.Yield();
        SetState(FirestoreConnectionState.Connecting, "Cloud: unreachable");
        string? credentialsPath = ResolveCredentialsPath();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            ProjectId = ReadProjectId(credentialsPath) ?? AppConfig.FirestoreProjectId;
            if (string.IsNullOrWhiteSpace(ProjectId))
            {
                throw new InvalidOperationException("Firestore project ID is unavailable.");
            }

            var clientBuilder = new FirestoreClientBuilder();
            if (!string.IsNullOrEmpty(credentialsPath))
            {
                ServiceAccountCredential credential =
                    await CredentialFactory.FromFileAsync<ServiceAccountCredential>(credentialsPath, timeout.Token);
                clientBuilder.GoogleCredential = credential.ToGoogleCredential();
            }

            FirestoreDb db = FirestoreDb.Create(ProjectId, await clientBuilder.BuildAsync(timeout.Token));
            await db.Collection("protected_assets").Limit(1).GetSnapshotAsync(timeout.Token);

            _db = db;
            SetState(FirestoreConnectionState.Connected, "Cloud: Connected");
            return db;
        }
        catch (Exception ex)
        {
            LogError($"Firestore connect failed (key: {credentialsPath ?? "none"})", ex);

            lock (_connectLock)
            {
                _connectTask = null;
            }
            SetState(
                string.IsNullOrEmpty(credentialsPath) ? FirestoreConnectionState.NotConfigured : FirestoreConnectionState.Unavailable,
                string.IsNullOrEmpty(credentialsPath) ? KeyNotFoundStatus : "Cloud: unreachable");
            Debug.WriteLine("[Firestore] Connection failed.");
            throw;
        }
    }

    public async Task RefreshAsync()
    {
        try
        {
            if (_db is null)
            {
                await GetDbAsync();
                return;
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await _db.Collection("protected_assets").Limit(1).GetSnapshotAsync(timeout.Token);
            SetState(FirestoreConnectionState.Connected, "Cloud: Connected");
        }
        catch (Exception ex)
        {
            LogError("Firestore connection check failed", ex);
            Debug.WriteLine("[Firestore] Connection check failed.");
        }
    }

    private static string? ResolveCredentialsPath()
    {
        // 1. Environment variable override
        string? configuredPath = Environment.GetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS");
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath.Trim()))
        {
            return Path.GetFullPath(configuredPath.Trim());
        }

        // 2. Per-user folder
        string localPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DAAMS", "serviceAccountKey.json");
        if (File.Exists(localPath))
        {
            return localPath;
        }

        // 3. Next to the exe (installed build)
        string besideExe = Path.Combine(AppContext.BaseDirectory, "serviceAccountKey.json");
        if (File.Exists(besideExe))
        {
            return besideExe;
        }

        // 4. Dev repo: walk up looking for monitoring_engine\serviceAccountKey.json
        string? dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            string devPath = Path.Combine(dir, "monitoring_engine", "serviceAccountKey.json");
            if (File.Exists(devPath))
            {
                return devPath;
            }
            dir = Directory.GetParent(dir)?.FullName;
        }

        return null;
    }

    private static string? ReadProjectId(string? credentialsPath)
    {
        if (string.IsNullOrEmpty(credentialsPath))
        {
            return null;
        }

        try
        {
            using FileStream stream = File.OpenRead(credentialsPath);
            using JsonDocument document = JsonDocument.Parse(stream);
            if (document.RootElement.TryGetProperty("project_id", out JsonElement projectId) &&
                projectId.ValueKind == JsonValueKind.String)
            {
                return projectId.GetString();
            }
        }
        catch (Exception ex)
        {
            LogError("Could not read project ID from credential file", ex);
            Debug.WriteLine("[Firestore] Could not read project ID from credential file.");
        }

        return null;
    }

    /// <summary>
    /// Appends an error to %LOCALAPPDATA%\DAAMS\controlcenter.log. Never throws.
    /// </summary>
    private static void LogError(string message, Exception ex)
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DAAMS");
            Directory.CreateDirectory(dir);
            File.AppendAllText(
                Path.Combine(dir, "controlcenter.log"),
                $"{DateTime.Now:u} {message}: {ex}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never break the app.
        }
    }

    private void SetState(FirestoreConnectionState state, string message)
    {
        if (State == state && StatusMessage == message)
        {
            return;
        }
        State = state;
        StatusMessage = message;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}