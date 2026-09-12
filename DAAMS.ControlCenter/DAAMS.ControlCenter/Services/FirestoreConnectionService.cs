using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Google.Cloud.Firestore;

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
    private FirestoreDb? _db;
    private Task<FirestoreDb>? _connectTask;

    public FirestoreConnectionState State { get; private set; } = FirestoreConnectionState.NotConfigured;
    public string StatusMessage { get; private set; } = "Firestore project not configured.";

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
        if (_db is not null)
        {
            return Task.FromResult(_db);
        }

        _connectTask ??= ConnectAsync();
        return _connectTask;
    }

    private async Task<FirestoreDb> ConnectAsync()
    {
        SetState(FirestoreConnectionState.Connecting, "Connecting to Firestore…");

        string? projectId = AppConfig.FirestoreProjectId;
        if (string.IsNullOrWhiteSpace(projectId))
        {
            _connectTask = null;
            SetState(FirestoreConnectionState.NotConfigured,
                "Firestore project ID not configured. Set Firestore:ProjectId in appsettings.json.");
            throw new InvalidOperationException(StatusMessage);
        }

        try
        {
            FirestoreDb db = await FirestoreDb.CreateAsync(projectId);

            // Lightweight round-trip so a bad project ID, missing/invalid
            // credentials, or no network surfaces right here — instead of
            // failing later, deep inside whichever ViewModel queries first.
            // Querying a real (possibly empty) collection is safe: Firestore
            // returns an empty snapshot for collections with no documents
            // rather than erroring, so this only fails on genuine connection
            // problems.
            await db.Collection("protected_assets").Limit(1).GetSnapshotAsync();

            _db = db;
            SetState(FirestoreConnectionState.Connected, $"Connected — project '{projectId}'");
            return db;
        }
        catch (Exception ex)
        {
            _connectTask = null;
            SetState(FirestoreConnectionState.Unavailable, "Firestore connection unavailable.");
            // Full exception detail goes to the debug log only — never to the UI.
            Debug.WriteLine($"[Firestore] Connection failed: {ex}");
            throw;
        }
    }

    private void SetState(FirestoreConnectionState state, string message)
    {
        State = state;
        StatusMessage = message;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
