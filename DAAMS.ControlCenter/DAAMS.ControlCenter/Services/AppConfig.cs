using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace ControlCenter.Services;

/// <summary>
/// Minimal, dependency-free configuration loader.
///
/// Reads Firestore:ProjectId from appsettings.json — a project ID is not a
/// secret, so this file is safe to commit — with an environment variable
/// override (DAAMS_FIRESTORE_PROJECT_ID) for per-developer/CI flexibility.
///
/// Credentials are NOT handled here and are never read from this file.
/// They're resolved entirely through Google's standard Application Default
/// Credentials chain (the GOOGLE_APPLICATION_CREDENTIALS environment variable,
/// or `gcloud auth application-default login`) — see README.md "Firestore
/// setup". This keeps every credential decision out of source code, XAML,
/// ViewModels, and this class.
/// </summary>
public static class AppConfig
{
    private static readonly Lazy<string?> _projectId = new(LoadProjectId);

    public static string? FirestoreProjectId => _projectId.Value;

    private static string? LoadProjectId()
    {
        string? fromEnv = Environment.GetEnvironmentVariable("DAAMS_FIRESTORE_PROJECT_ID");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return fromEnv;
        }

        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            if (!File.Exists(path))
            {
                return null;
            }

            using FileStream stream = File.OpenRead(path);
            using JsonDocument doc = JsonDocument.Parse(stream);

            if (doc.RootElement.TryGetProperty("Firestore", out JsonElement firestoreEl) &&
                firestoreEl.TryGetProperty("ProjectId", out JsonElement idEl) &&
                idEl.ValueKind == JsonValueKind.String)
            {
                string value = idEl.GetString() ?? string.Empty;
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
        }
        catch (Exception ex)
        {
            // Configuration problems should never crash the app at startup —
            // FirestoreConnectionService will surface "not configured" instead.
            Debug.WriteLine($"[AppConfig] Failed to read appsettings.json: {ex}");
        }

        return null;
    }
}
