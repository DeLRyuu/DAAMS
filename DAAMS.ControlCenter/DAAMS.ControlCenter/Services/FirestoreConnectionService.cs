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
/// Credentials are resolved entirely through Google's Application Default
/// Credentials chain — this class never reads, stores, or requests a
/// credential file path itself. See README.md "Firestore setup".
/// </summary>
public class FirestoreConnectionService
{
    private const string KeyNotFoundStatus =
        "Cloud: key not found (put serviceAccountKey.json in %LOCALAPPDATA%\\DAAMS)";
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
        catch (Exception)
        {
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
        catch (Exception)
        {
            Debug.WriteLine("[Firestore] Connection check failed.");
        }
    }

    private static string? ResolveCredentialsPath()
    {
        string? configuredPath = Environment.GetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS");
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath.Trim()))
        {
            return Path.GetFullPath(configuredPath.Trim());
        }

        string localPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DAAMS", "serviceAccountKey.json");
        if (File.Exists(localPath))
        {
            return localPath;
        }

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
        catch (Exception)
        {
            Debug.WriteLine("[Firestore] Could not read project ID from credential file.");
        }

        return null;
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
