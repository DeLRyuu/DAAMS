using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using ControlCenter.Models;
using Google.Cloud.Firestore;

namespace ControlCenter.Services;

/// <summary>
/// Live (listener-based) cache + write helpers for FirestoreService.
///
/// Why a listener: the old code re-read the WHOLE collection (GetSnapshotAsync) on every
/// refresh, so every document cost one read each time. A snapshot listener reads each
/// collection once, then only pays for documents that change. The data is kept in memory,
/// so refreshing a screen from the cache costs no Firestore reads at all.
///
/// Why writes: the Control Center must be able to push alert status and incident changes
/// to Firestore, otherwise those changes never leave a PC that has no Monitoring Engine.
/// Writes use merge, so fields written by the Engine are never wiped.
/// </summary>
public partial class FirestoreService
{
    private const string FirestoreTimeFormat = "yyyy-MM-dd HH:mm:ss";

    /// <summary>Only the newest N activity logs are listened to, so opening the app never reads the entire history.</summary>
    private const int ActivityLiveLimit = 2000;

    private sealed class LiveCollection<T>
    {
        private readonly object _gate = new();
        private List<T>? _items;

        public bool Ready { get { lock (_gate) { return _items is not null; } } }

        public List<T> Snapshot()
        {
            lock (_gate) { return _items is null ? new List<T>() : new List<T>(_items); }
        }

        public void ReplaceAll(List<T> items)
        {
            lock (_gate) { _items = items; }
        }

        /// <summary>Optimistic patch after our own write. Ignored until the first snapshot has arrived.</summary>
        public void Upsert(Func<T, bool> match, T item)
        {
            lock (_gate)
            {
                if (_items is null) return;
                int index = _items.FindIndex(x => match(x));
                if (index >= 0) _items[index] = item; else _items.Add(item);
            }
        }
    }

    private readonly LiveCollection<ProtectedAsset> _liveAssets = new();
    private readonly LiveCollection<ActivityLog> _liveActivity = new();
    private readonly LiveCollection<SecurityAlert> _liveAlerts = new();
    private readonly LiveCollection<IncidentReport> _liveIncidents = new();
    private readonly List<FirestoreChangeListener> _listeners = new();
    private readonly object _liveLock = new();
    private bool _liveStarted;

    /// <summary>True once every collection has received its first snapshot.</summary>
    public bool LiveReady => _liveAssets.Ready && _liveActivity.Ready && _liveAlerts.Ready && _liveIncidents.Ready;

    /// <summary>Raised (on a background thread) whenever the cloud data changed.</summary>
    public event EventHandler? LiveDataChanged;

    public static string FormatTime(DateTime value) => value.ToString(FirestoreTimeFormat, CultureInfo.InvariantCulture);

    /// <summary>
    /// Starts the four listeners once. Safe to call repeatedly (every refresh tick): it does nothing
    /// when already running, and does nothing while the PC has no network. Throws if Firestore cannot be reached.
    /// </summary>
    public async Task EnsureLiveAsync()
    {
        lock (_liveLock)
        {
            if (_liveStarted) return;
        }

        if (!NetworkInterface.GetIsNetworkAvailable()) return;

        FirestoreDb db = await _connection.GetDbAsync();

        lock (_liveLock)
        {
            if (_liveStarted) return;
            _liveStarted = true;

            _listeners.Add(Listen(db.Collection(ProtectedAssetsCollection), _liveAssets, MapProtectedAsset));
            _listeners.Add(Listen(
                db.Collection(ActivityLogsCollection).OrderByDescending("timestamp").Limit(ActivityLiveLimit),
                _liveActivity,
                MapActivityLog));
            _listeners.Add(Listen(db.Collection(SecurityAlertsCollection), _liveAlerts, MapSecurityAlert));
            _listeners.Add(Listen(db.Collection(IncidentReportsCollection), _liveIncidents, MapIncidentReport));
        }
    }

    private FirestoreChangeListener Listen<T>(Query query, LiveCollection<T> cache, Func<DocumentSnapshot, T> map)
    {
        FirestoreChangeListener listener = query.Listen(snapshot =>
        {
            try
            {
                // snapshot.Documents is the full current result set, so just rebuild the in-memory list.
                cache.ReplaceAll(snapshot.Documents.Select(map).ToList());
                LiveDataChanged?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FirestoreService] Live snapshot handling failed: {ex}");
            }
        });

        // If a listener dies (for example revoked key), stop all of them so EnsureLiveAsync can start fresh.
        _ = listener.ListenerTask.ContinueWith(_ => ResetLive(), TaskScheduler.Default);
        return listener;
    }

    private void ResetLive()
    {
        List<FirestoreChangeListener> toStop;
        lock (_liveLock)
        {
            toStop = new List<FirestoreChangeListener>(_listeners);
            _listeners.Clear();
            _liveStarted = false;
        }

        foreach (FirestoreChangeListener listener in toStop)
        {
            _ = listener.StopAsync();
        }
    }

    /// <summary>
    /// Merge-writes fields into one document (10 s timeout). Throws when Firestore is unreachable so the
    /// caller can keep the local row PENDING. On success the document is re-read into the live cache so
    /// the screen shows the change immediately instead of waiting for the listener.
    /// </summary>
    public async Task SetDocumentAsync(string collection, string documentId, Dictionary<string, object?> fields)
    {
        FirestoreDb db = await _connection.GetDbAsync();
        DocumentReference reference = db.Collection(collection).Document(documentId);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await reference.SetAsync(fields, SetOptions.MergeAll, timeout.Token);

        await RefreshIntoCacheAsync(reference);
    }

    private async Task RefreshIntoCacheAsync(DocumentReference reference)
    {
        try
        {
            DocumentSnapshot doc = await reference.GetSnapshotAsync();
            if (!doc.Exists) return;

            string collection = reference.Parent.Id;
            if (collection == SecurityAlertsCollection)
            {
                SecurityAlert alert = MapSecurityAlert(doc);
                _liveAlerts.Upsert(a => a.AlertId == alert.AlertId, alert);
            }
            else if (collection == IncidentReportsCollection)
            {
                IncidentReport incident = MapIncidentReport(doc);
                _liveIncidents.Upsert(i => i.IncidentId == incident.IncidentId, incident);
            }
            else
            {
                return;
            }

            LiveDataChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            // Best effort only: the listener will deliver the same change a moment later.
            Debug.WriteLine($"[FirestoreService] Cache refresh after write failed: {ex.Message}");
        }
    }
}
